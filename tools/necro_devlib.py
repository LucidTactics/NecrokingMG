"""Shared helpers for driving the Necroking dev supervisor (tools/devserver.py).

Single source of truth used by BOTH:
  * tools/necro_mcp.py — the MCP server (typed tools, no shell) — primary path
  * tools/devctl.py     — the CLI fallback (one allowlisted bash command)

Talks to the supervisor over HTTP on port 8777 and auto-starts it (detached, so
it survives between calls) plus the game. Raises RuntimeError on failure; callers
decide how to surface it (the MCP server turns it into an isError tool result, the
CLI prints to stderr and exits non-zero). stdlib only — no third-party deps.
"""

import json
import os
import subprocess
import sys
import time
import urllib.request

SUP_PORT = 8777
HOST = "127.0.0.1"
BASE = f"http://{HOST}:{SUP_PORT}"

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEVSERVER = os.path.join(REPO_ROOT, "tools", "devserver.py")
SCREENSHOT_DIR = os.path.join(REPO_ROOT, "bin", "Debug", "log", "screenshots")
LOG_DIR = os.path.join(REPO_ROOT, "bin", "Debug", "log")


def req(method, path, payload=None, timeout=60, raw_bytes=False):
    """HTTP request to the supervisor. Returns parsed JSON (or raw bytes)."""
    data = None
    headers = {}
    if payload is not None:
        data = json.dumps(payload).encode()
        headers["Content-Type"] = "application/json"
    r = urllib.request.Request(BASE + path, data=data, headers=headers, method=method)
    with urllib.request.urlopen(r, timeout=timeout) as resp:
        body = resp.read()
        return body if raw_bytes else json.loads(body.decode())


def supervisor_up():
    try:
        req("GET", "/status", timeout=2)
        return True
    except Exception:
        return False


def ensure_supervisor():
    """Start tools/devserver.py detached if it isn't already answering on 8777."""
    if supervisor_up():
        return
    os.makedirs(LOG_DIR, exist_ok=True)
    # Distinct from the game's own DebugLog file (log/devserver.log) — sharing the
    # name would lock that file and crash the game on startup.
    logf = open(os.path.join(LOG_DIR, "devctl-supervisor.log"), "ab")
    kwargs = dict(stdout=logf, stderr=logf, stdin=subprocess.DEVNULL,
                  cwd=REPO_ROOT, close_fds=True)
    if os.name == "nt":
        # DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP — fully detach so the
        # supervisor outlives this short-lived process and every later call.
        kwargs["creationflags"] = 0x00000008 | 0x00000200
    else:
        kwargs["start_new_session"] = True
    subprocess.Popen([sys.executable, DEVSERVER], **kwargs)
    deadline = time.time() + 20
    while time.time() < deadline:
        if supervisor_up():
            return
        time.sleep(0.4)
    raise RuntimeError(
        "supervisor did not come up within 20s; see "
        + os.path.join(LOG_DIR, "devctl-supervisor.log"))


def ensure_game(windowed=False, map_name=None):
    """Make sure the game process is running (idempotent). Returns /status-ish dict."""
    st = req("GET", "/status")
    if st.get("game_running"):
        return st
    if not st.get("exe_exists"):
        raise RuntimeError(
            "game exe not built. Build first (restart with build, or "
            "`dotnet build Necroking/Necroking.csproj`).")
    body = {"windowed": windowed}
    if map_name:
        body["map"] = map_name
    return req("POST", "/game/start", body, timeout=90)


def send_cmd(cmd, args=None, opts=None):
    """Forward a {cmd,args,opts} game command to the running game. Ensures up first."""
    ensure_supervisor()
    ensure_game()
    payload = {"cmd": cmd, "args": [str(a) for a in (args or [])]}
    if opts:
        payload["opts"] = opts
    return req("POST", "/cmd", payload, timeout=60)


def screenshot(name="shot", opts=None):
    """Trigger a screenshot in the game; return the absolute PNG path."""
    ensure_supervisor()
    ensure_game()
    payload = {"cmd": "screenshot", "args": [name]}
    if opts:
        payload["opts"] = opts
    req("POST", "/cmd", payload, timeout=60)
    return os.path.join(SCREENSHOT_DIR, name + ".png")
