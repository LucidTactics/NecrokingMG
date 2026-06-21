#!/usr/bin/env python3
"""Necroking dev MCP server — typed tools for driving the running game.

This is the safe, no-shell equivalent of the desktop app's Claude_Preview tools,
usable in surfaces (the VS Code extension) where those aren't connected. Instead of
composing bash + curl (which can't be reliably whitelisted), Claude calls structured
tools: necro_status / necro_start / necro_cmd / necro_screenshot / necro_restart /
necro_stop. necro_screenshot returns the frame inline as an image.

Registered in .mcp.json as the server "necroking". It is **dependency-free**
(stdlib only, JSON-RPC 2.0 over stdio) so it runs anywhere Python does — no pip
installs. It drives the same supervisor as tools/devctl.py via tools/necro_devlib.py.

STDOUT IS THE PROTOCOL CHANNEL: only newline-delimited JSON-RPC messages may go to
stdout. All diagnostics go to stderr (which Claude Code captures to the MCP log).
"""

import base64
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import necro_devlib as dev  # noqa: E402

PROTO_DEFAULT = "2024-11-05"
SERVER_INFO = {"name": "necroking", "version": "1.0.0"}

TOOLS = [
    {
        "name": "necro_status",
        "description": "Get the dev supervisor + game status (is the game running, "
                       "pid, last build). Auto-starts the supervisor if needed. "
                       "Does NOT start the game.",
        "inputSchema": {"type": "object", "properties": {}},
    },
    {
        "name": "necro_start",
        "description": "Ensure the game is running (auto-starts supervisor + game). "
                       "Headless 1280x720 by default.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "windowed": {"type": "boolean",
                             "description": "Show a real window instead of headless."},
                "map": {"type": "string",
                        "description": "Map name to load into gameplay on start."},
            },
        },
    },
    {
        "name": "necro_cmd",
        "description": "Run a game dev command and return its JSON reply. This is the "
                       "main driver — the same {cmd,args,opts} commands the dashboard "
                       "uses (ExecuteDevCommand in Game1.cs). Auto-starts everything. "
                       "Examples: cmd='state'; cmd='menu' args=['new_game']; "
                       "cmd='spawn' args=['Skeleton','2090','1882']; cmd='camera' "
                       "args=['2096','1882','48']; cmd='speed' args=['4']; cmd='help' "
                       "to list every command + selector syntax.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "cmd": {"type": "string", "description": "Game command name."},
                "args": {"type": "array", "items": {"type": "string"},
                         "description": "Positional args (numbers as strings)."},
                "opts": {"type": "object",
                         "description": "Named options for the command."},
            },
            "required": ["cmd"],
        },
    },
    {
        "name": "necro_screenshot",
        "description": "Capture the live game frame and return it inline as an image "
                       "(also saved to bin/Debug/log/screenshots/<name>.png). "
                       "Auto-starts everything.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "name": {"type": "string", "description": "File name (no extension). "
                                                          "Default 'mcp_shot'."},
                "no_ui": {"type": "boolean", "description": "Hide the HUD."},
                "no_ground": {"type": "boolean",
                              "description": "Drop ground+grass (scenario black look)."},
                "full": {"type": "boolean",
                         "description": "Return full 1280x720 instead of downsampled."},
                "size": {"type": "string",
                         "description": "Explicit WxH downsample (e.g. '960x540'). "
                                        "Ignored if full=true. Default '640x360'."},
            },
        },
    },
    {
        "name": "necro_restart",
        "description": "Stop the game, optionally rebuild the C# project, then start "
                       "it again. Use after a code change. Build errors come back in "
                       "the JSON ('build').",
        "inputSchema": {
            "type": "object",
            "properties": {
                "build": {"type": "boolean",
                          "description": "Rebuild before restarting."},
            },
        },
    },
    {
        "name": "necro_stop",
        "description": "Stop the game process (leaves the cheap supervisor running). "
                       "Do this when finished so a headless game doesn't idle.",
        "inputSchema": {"type": "object", "properties": {}},
    },
]


def _result(text=None, image_b64=None, mime="image/png", is_error=False):
    content = []
    if image_b64 is not None:
        content.append({"type": "image", "data": image_b64, "mimeType": mime})
    if text is not None:
        content.append({"type": "text", "text": text})
    return {"content": content, "isError": is_error}


def _json(obj):
    return json.dumps(obj, indent=2)


def call_tool(name, args):
    if name == "necro_status":
        dev.ensure_supervisor()
        return _result(_json(dev.req("GET", "/status")))

    if name == "necro_start":
        dev.ensure_supervisor()
        st = dev.ensure_game(windowed=bool(args.get("windowed")),
                             map_name=args.get("map"))
        return _result(_json(st))

    if name == "necro_cmd":
        cmd = args.get("cmd")
        if not cmd:
            return _result("necro_cmd requires 'cmd'.", is_error=True)
        reply = dev.send_cmd(cmd, args.get("args"), args.get("opts"))
        return _result(_json(reply))

    if name == "necro_screenshot":
        opts = {}
        if args.get("no_ui"):
            opts["no_ui"] = True
        if args.get("no_ground"):
            opts["no_ground"] = True
        opts["downsample_to"] = "full" if args.get("full") else (args.get("size") or "640x360")
        shot_name = args.get("name") or "mcp_shot"
        path = dev.screenshot(shot_name, opts)
        with open(path, "rb") as f:
            b64 = base64.b64encode(f.read()).decode()
        return _result(text=f"screenshot saved: {path}", image_b64=b64)

    if name == "necro_restart":
        dev.ensure_supervisor()
        return _result(_json(dev.req("POST", "/game/restart",
                                     {"build": bool(args.get("build"))}, timeout=600)))

    if name == "necro_stop":
        dev.ensure_supervisor()
        return _result(_json(dev.req("POST", "/game/stop", {})))

    return _result(f"unknown tool: {name}", is_error=True)


def send(obj):
    sys.stdout.write(json.dumps(obj) + "\n")
    sys.stdout.flush()


def main():
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except Exception:
            continue
        mid = msg.get("id")
        method = msg.get("method")

        if method == "initialize":
            params = msg.get("params") or {}
            send({"jsonrpc": "2.0", "id": mid, "result": {
                "protocolVersion": params.get("protocolVersion", PROTO_DEFAULT),
                "capabilities": {"tools": {}},
                "serverInfo": SERVER_INFO,
            }})
        elif method in ("notifications/initialized", "initialized"):
            pass  # notification — no reply
        elif method == "ping":
            send({"jsonrpc": "2.0", "id": mid, "result": {}})
        elif method == "tools/list":
            send({"jsonrpc": "2.0", "id": mid, "result": {"tools": TOOLS}})
        elif method == "tools/call":
            params = msg.get("params") or {}
            try:
                res = call_tool(params.get("name"), params.get("arguments") or {})
            except Exception as e:
                res = _result(f"{type(e).__name__}: {e}", is_error=True)
            send({"jsonrpc": "2.0", "id": mid, "result": res})
        elif method in ("resources/list", "prompts/list"):
            key = "resources" if method.startswith("resources") else "prompts"
            send({"jsonrpc": "2.0", "id": mid, "result": {key: []}})
        elif mid is not None:
            send({"jsonrpc": "2.0", "id": mid,
                  "error": {"code": -32601, "message": f"method not found: {method}"}})
        # unknown notifications (no id) are ignored


if __name__ == "__main__":
    main()
