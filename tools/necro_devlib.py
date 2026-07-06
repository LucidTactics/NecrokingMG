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
import re
import subprocess
import sys
import time
import urllib.request

SUP_PORT = 8777
HOST = "127.0.0.1"
BASE = f"http://{HOST}:{SUP_PORT}"

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEVSERVER = os.path.join(REPO_ROOT, "tools", "devserver.py")

# The supervisor (tools/devserver.py) owns where the running game writes screenshots/logs
# and is the SOURCE OF TRUTH — screenshot() asks it via /status. These locals are only the
# pre-supervisor default (the bootstrap log) and a fallback if /status is unreachable; they
# must mirror the supervisor's dedicated output folder (bin/Devbuild, overridable with
# NECRO_DEV_BUILDDIR) so the two never disagree on the path. The preview builds into its
# own folder — separate from the user's bin/Debug|bin/Release manual test builds — so a dev
# rebuild never clobbers a build the user is hand-testing.
BUILD_DIR = os.environ.get("NECRO_DEV_BUILDDIR", os.path.join(REPO_ROOT, "bin", "Devbuild"))
SCREENSHOT_DIR = os.path.join(BUILD_DIR, "log", "screenshots")
LOG_DIR = os.path.join(BUILD_DIR, "log")


def screenshot_dir():
    """Absolute dir the running game writes screenshots to. Authoritative value comes
    from the supervisor's /status (it reports the config it actually launched); falls
    back to the local default if the supervisor can't be reached."""
    try:
        d = req("GET", "/status", timeout=5).get("screenshot_dir")
        if d:
            return d
    except Exception:
        pass
    return SCREENSHOT_DIR


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


# --- supervisor lifecycle ownership -----------------------------------------
# Two callers want opposite lifetimes for the supervisor:
#   * the CLI (devctl.py) is short-lived per call, so it DETACHES the supervisor
#     (default) — it must persist between separate command invocations.
#   * the MCP server (necro_mcp.py) is long-lived (lives as long as the editor),
#     so it OWNS the supervisor via a Windows Job Object with KILL_ON_JOB_CLOSE,
#     mirroring how the desktop app owns its supervisor: close the editor -> the
#     MCP process dies -> the OS kills the supervisor -> the supervisor's own job
#     kills the game. No orphan left running forever.
# necro_mcp.py sets OWN_BY_DEFAULT = True at import; ensure_supervisor(own=None)
# then defaults to it. Best-effort: any failure falls back to detached behavior.
OWN_BY_DEFAULT = False
_owned_job = None  # Windows job handle held for this process's lifetime


def _ensure_job():
    global _owned_job
    if os.name != "nt":
        return None
    if _owned_job is not None:
        return _owned_job
    try:
        import ctypes
        from ctypes import wintypes
        k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        k32.CreateJobObjectW.restype = wintypes.HANDLE
        k32.CreateJobObjectW.argtypes = [wintypes.LPVOID, wintypes.LPCWSTR]
        job = k32.CreateJobObjectW(None, None)
        if not job:
            return None

        class BASIC(ctypes.Structure):
            _fields_ = [
                ("PerProcessUserTimeLimit", ctypes.c_int64),
                ("PerJobUserTimeLimit", ctypes.c_int64),
                ("LimitFlags", wintypes.DWORD),
                ("MinimumWorkingSetSize", ctypes.c_size_t),
                ("MaximumWorkingSetSize", ctypes.c_size_t),
                ("ActiveProcessLimit", wintypes.DWORD),
                ("Affinity", ctypes.c_size_t),
                ("PriorityClass", wintypes.DWORD),
                ("SchedulingClass", wintypes.DWORD),
            ]

        class EXT(ctypes.Structure):
            _fields_ = [
                ("BasicLimitInformation", BASIC),
                ("IoInfo", ctypes.c_uint64 * 6),
                ("ProcessMemoryLimit", ctypes.c_size_t),
                ("JobMemoryLimit", ctypes.c_size_t),
                ("PeakProcessMemoryUsed", ctypes.c_size_t),
                ("PeakJobMemoryUsed", ctypes.c_size_t),
            ]

        ext = EXT()
        ext.BasicLimitInformation.LimitFlags = 0x2000  # KILL_ON_JOB_CLOSE
        k32.SetInformationJobObject.argtypes = [
            wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
        if not k32.SetInformationJobObject(job, 9, ctypes.byref(ext), ctypes.sizeof(ext)):
            return None
        _owned_job = job  # never CloseHandle'd: closes on process exit -> kill fires
        return _owned_job
    except Exception:
        return None


def _own_supervisor(handle):
    """Put the supervisor (a Popen process handle) into our kill-on-close job so it
    dies when THIS process exits. Best-effort, Windows only.

    We only ever own a supervisor we START ourselves — never one we merely find
    already running. The supervisor is shared (one per machine, port 8777) across
    however many editor sessions are open; if a session adopted a supervisor it
    didn't start, closing that session would kill it out from under the others."""
    job = _ensure_job()
    if not job:
        return
    try:
        import ctypes
        from ctypes import wintypes
        k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        k32.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
        k32.AssignProcessToJobObject(job, wintypes.HANDLE(handle))
    except Exception:
        pass


def ensure_supervisor(own=None):
    """Ensure tools/devserver.py is answering on 8777, starting it if needed.

    own=True  -> tie the supervisor's lifetime to THIS process (MCP server).
    own=False -> detach it so it persists between calls (CLI).
    own=None  -> use OWN_BY_DEFAULT.
    """
    if own is None:
        own = OWN_BY_DEFAULT
    if supervisor_up():
        # Already running (started by another editor session, the CLI, or a prior
        # run). Do NOT take ownership of a supervisor we didn't start — see
        # _own_supervisor. We just use it; whoever started it owns its lifetime.
        return
    os.makedirs(LOG_DIR, exist_ok=True)
    # Distinct from the game's own DebugLog file (log/devserver.log) — sharing the
    # name would lock that file and crash the game on startup.
    logf = open(os.path.join(LOG_DIR, "devctl-supervisor.log"), "ab")
    kwargs = dict(stdout=logf, stderr=logf, stdin=subprocess.DEVNULL,
                  cwd=REPO_ROOT, close_fds=True)
    if os.name == "nt":
        # DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP — own console/group. (Job
        # membership, set below for own=True, is independent of this and still
        # kills it on our exit.)
        kwargs["creationflags"] = 0x00000008 | 0x00000200
    else:
        # CLI wants it to outlive us (new session); owned mode keeps it in our
        # group so it goes down with us.
        kwargs["start_new_session"] = not own
    proc = subprocess.Popen([sys.executable, DEVSERVER], **kwargs)
    if own and os.name == "nt":
        _own_supervisor(int(proc._handle))
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
        # NB: a plain `dotnet build` outputs to bin/Debug — it does NOT produce the
        # preview's exe (bin/Devbuild, Release config). Only the supervisor build does.
        raise RuntimeError(
            "game exe not built (" + st.get("exe", "bin/Devbuild/Necroking.exe") + "). "
            "Use necro_restart with build:true, or `python tools/devctl.py restart "
            "--build` — a plain `dotnet build` outputs to bin/Debug, not the "
            "preview's build dir.")
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
    """Trigger a screenshot in the game; return the absolute PNG path.

    Raises RuntimeError instead of returning a path when the capture failed —
    the game's reply is checked, and the PNG's mtime is compared against the
    request time. Without those checks a failed capture would silently hand
    back a STALE frame from an earlier shot with the same name (the default
    name is reused), which is the worst failure mode for visual verification."""
    # The game writes log/screenshots/<name>.png relative to its cwd; keep the
    # name a plain filename so the write can't wander (and the path we return
    # matches where it actually landed).
    if not re.fullmatch(r"[A-Za-z0-9_.-]+", name or ""):
        raise RuntimeError(
            f"invalid screenshot name {name!r}: use letters, digits, '_', '-', '.'")
    ensure_supervisor()
    ensure_game()
    payload = {"cmd": "screenshot", "args": [name]}
    if opts:
        payload["opts"] = opts
    t0 = time.time()
    reply = req("POST", "/cmd", payload, timeout=60)
    if not (isinstance(reply, dict) and reply.get("ok")):
        err = reply.get("error", reply) if isinstance(reply, dict) else reply
        raise RuntimeError(f"screenshot command failed: {err}")
    path = os.path.join(screenshot_dir(), name + ".png")
    if not os.path.exists(path):
        raise RuntimeError(f"screenshot reported ok but PNG not found: {path}")
    # 2s slop covers filesystem mtime granularity.
    if os.path.getmtime(path) < t0 - 2:
        raise RuntimeError(
            f"screenshot PNG is stale (predates this request): {path}")
    return path
