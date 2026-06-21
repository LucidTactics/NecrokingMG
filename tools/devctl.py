#!/usr/bin/env python3
"""
devctl — CLI fallback for driving the Necroking dev preview.

This is tier 3 of the preview stack (see docs/devpreview.md):
  1. Claude_Preview MCP tools  — desktop Claude Code app only
  2. necroking MCP server      — tools/necro_mcp.py (primary elsewhere, typed/no-shell)
  3. devctl.py (this file)     — one allowlisted bash command, for when the MCP
                                 server isn't loaded

All three drive the SAME supervisor (tools/devserver.py, port 8777) and game, via the
shared tools/necro_devlib.py. This wrapper auto-starts the supervisor (detached) and the
game so a single command works from a cold start.

It deliberately does NOT modify devserver.py (that file's policy forbids casual edits).
New *game* commands belong in C# (ExecuteDevCommand); /cmd forwards {cmd,args,opts}
verbatim, so this wrapper needs no changes when new game commands are added.

USAGE (all auto-start the supervisor + game as needed):
    python tools/devctl.py status
    python tools/devctl.py up [--windowed] [--map default]
    python tools/devctl.py cmd <gamecmd> [arg ...] [key=value ...]
    python tools/devctl.py state | units | help        # aliases for: cmd <name>
    python tools/devctl.py shot [name] [no_ui=true] [downsample_to=full]
    python tools/devctl.py raw '<json>'                # POST arbitrary {cmd,args,opts}
    python tools/devctl.py restart [--build]
    python tools/devctl.py build
    python tools/devctl.py pin [label] | pin --clear
    python tools/devctl.py down                          # stop the game
    python tools/devctl.py kill-server                   # stop game AND supervisor

`shot` prints the absolute PNG path prefixed `SHOT: ` — read that file to inspect the
frame. Exit code is 0 on ok:true, 1 otherwise.
"""

import json
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import necro_devlib as dev  # noqa: E402


def emit(obj):
    print(json.dumps(obj, indent=2))
    ok = obj.get("ok", True) if isinstance(obj, dict) else True
    sys.exit(0 if ok else 1)


def parse_cmd_tokens(tokens):
    """bare token -> positional arg, key=value -> opt."""
    args, opts = [], {}
    for t in tokens:
        i = t.find("=")
        if i > 0:
            opts[t[:i]] = t[i + 1:]
        else:
            args.append(t)
    return args, opts


def main(argv):
    if not argv:
        print(__doc__)
        sys.exit(0)
    sub, rest = argv[0], argv[1:]

    try:
        if sub == "status":
            dev.ensure_supervisor()
            emit(dev.req("GET", "/status"))

        if sub in ("up", "start"):
            windowed = "--windowed" in rest
            map_name = rest[rest.index("--map") + 1] if "--map" in rest else None
            dev.ensure_supervisor()
            emit(dev.ensure_game(windowed=windowed, map_name=map_name))

        if sub in ("down", "stop"):
            dev.ensure_supervisor()
            emit(dev.req("POST", "/game/stop", {}))

        if sub == "kill-server":
            if dev.supervisor_up():
                try:
                    dev.req("POST", "/game/stop", {})
                except Exception:
                    pass
            if os.name == "nt":
                subprocess.run(
                    ["powershell", "-NoProfile", "-Command",
                     "Get-NetTCPConnection -LocalPort 8777 -State Listen "
                     "-ErrorAction SilentlyContinue | ForEach-Object { "
                     "Stop-Process -Id $_.OwningProcess -Force "
                     "-ErrorAction SilentlyContinue }"],
                    capture_output=True)
            else:
                subprocess.run(["pkill", "-f", "tools/devserver.py"], capture_output=True)
            emit({"ok": True, "result": "supervisor stopped"})

        if sub == "build":
            dev.ensure_supervisor()
            emit(dev.req("POST", "/build", {}, timeout=600))

        if sub == "restart":
            dev.ensure_supervisor()
            emit(dev.req("POST", "/game/restart", {"build": "--build" in rest}, timeout=600))

        if sub == "pin":
            dev.ensure_supervisor()
            if "--clear" in rest:
                emit(dev.req("POST", "/pin", {"clear": True}))
            dev.ensure_game()
            emit(dev.req("POST", "/pin", {"label": rest[0] if rest else ""}))

        if sub in ("cmd", "state", "units", "help"):
            if sub == "cmd":
                if not rest:
                    sys.stderr.write("devctl cmd: need a game command name\n")
                    sys.exit(1)
                gamecmd, tokens = rest[0], rest[1:]
            else:
                gamecmd, tokens = sub, rest
            args, opts = parse_cmd_tokens(tokens)
            emit(dev.send_cmd(gamecmd, args, opts))

        if sub == "raw":
            if not rest:
                sys.stderr.write("devctl raw: need a JSON payload\n")
                sys.exit(1)
            dev.ensure_supervisor()
            dev.ensure_game()
            emit(dev.req("POST", "/cmd", json.loads(rest[0]), timeout=60))

        if sub == "shot":
            name = "devshot"
            tokens = rest
            if rest and "=" not in rest[0]:
                name, tokens = rest[0], rest[1:]
            _args, opts = parse_cmd_tokens(tokens)
            path = dev.screenshot(name, opts)
            if os.path.exists(path):
                print(f"SHOT: {os.path.abspath(path)}")
                sys.exit(0)
            sys.stderr.write(f"devctl shot: expected PNG not found at {path}\n")
            sys.exit(1)

    except RuntimeError as e:
        sys.stderr.write(f"devctl: {e}\n")
        sys.exit(1)

    sys.stderr.write(f"devctl: unknown subcommand '{sub}'\n\n{__doc__}\n")
    sys.exit(2)


if __name__ == "__main__":
    main(sys.argv[1:])
