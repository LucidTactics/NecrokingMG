#!/usr/bin/env python3
"""
Lean dev supervisor for Necroking.

A *persistent* HTTP server that owns the game process lifecycle so the game can
be rebuilt and relaunched without ever restarting this server. Claude (or you)
talks only to this server; it forwards gameplay commands to the running game's
in-process dev listener (Necroking/Dev/DevServer.cs, enabled with --devserver).

    Claude  --curl-->  this supervisor (:8777)  --proxy-->  game (:8778)

Run it (typically in the background):

    python tools/devserver.py            # serves on :8777, game on :8778

Endpoints (all POST unless noted), responses are JSON:

    GET  /status                      is the game running? pid? last build?
    POST /game/start  {"windowed":bool,"map":str}   launch + wait until ready
    POST /game/stop                   kill the game process
    POST /game/restart {"build":bool} stop -> (optional build) -> start
    POST /build                       stop game, dotnet build, return errors
    POST /cmd  {"cmd":..,"args":[..]} forward to the running game, return reply

Game commands currently understood (see ExecuteDevCommand in Game1.cs):
    ping | state | start_game [map] | spawn <type> <x> <y> |
    camera <x> <y> [zoom] | speed <n> | pause | resume | screenshot [name]
"""

import json
import os
import subprocess
import sys
import time
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

SUPERVISOR_PORT = 8777
GAME_PORT = 8778
DEFAULT_RESOLUTION = "1280x720"  # game renders at this; screenshots downsample on return

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROJECT = os.path.join(REPO_ROOT, "Necroking", "Necroking.csproj")
EXE = os.path.join(REPO_ROOT, "bin", "Debug", "Necroking.exe")
SCREENSHOT_DIR = os.path.join(os.path.dirname(EXE), "log", "screenshots")
LIVE_FRAME = "live"  # screenshot name reused for the dashboard's live view

# Dashboard served at GET / — preview_start renders this; preview_eval runs JS
# here (window.dev) to drive the game, preview_screenshot captures the frame.
DASHBOARD_HTML = """<!doctype html><html><head><meta charset=utf-8>
<title>Necroking Dev</title><style>
  body{margin:0;background:#15101e;color:#cbb;font:13px ui-monospace,monospace}
  header{padding:6px 10px;background:#1f1830;color:#caa;display:flex;gap:12px;align-items:center}
  #status{color:#8c8}
  #frame{display:block;max-width:100%;image-rendering:pixelated;background:#000;margin:0 auto}
  #bar{padding:6px 10px;display:flex;gap:6px}
  #cmd{flex:1;background:#0e0a16;color:#dcd;border:1px solid #443;padding:4px 6px}
  button{background:#2a2140;color:#dcd;border:1px solid #554;padding:4px 10px;cursor:pointer}
  #log{height:120px;overflow:auto;padding:6px 10px;white-space:pre-wrap;color:#9a9;border-top:1px solid #332}
</style></head><body>
<header><b>Necroking Dev</b><span id=status>connecting…</span></header>
<img id=frame>
<div id=bar>
  <input id=cmd placeholder='spawn Skeleton 2094 1880'>
  <button onclick=runInput()>Send</button>
</div>
<div id=log></div>
<script>
const logEl=document.getElementById('log'), statusEl=document.getElementById('status');
function log(m){logEl.textContent+=m+"\\n";logEl.scrollTop=logEl.scrollHeight;}
// Transparent pass-through: send any {cmd,args,opts} struct. Neither this helper
// nor the supervisor needs to know the command set — add commands in C# only.
window.devRaw=async(payload)=>{
  const r=await fetch('/cmd',{method:'POST',headers:{'Content-Type':'application/json'},
    body:JSON.stringify(payload)});
  const j=await r.json(); window.__last=j;
  log('> '+JSON.stringify(payload)+' => '+JSON.stringify(j)); return j;
};
// window.dev(cmd, args, opts) -> Promise<result JSON>. Driven by preview_eval.
window.dev=(cmd,args=[],opts={})=>{
  const p={cmd,args}; if(opts&&Object.keys(opts).length)p.opts=opts; return window.devRaw(p);
};
// Manual box: tokens with '=' become opts, the rest positional args. e.g.
//   screenshot name=foo no_ui=true downsample_to=full
function runInput(){const v=document.getElementById('cmd').value.trim();if(!v)return;
  const t=v.split(/\\s+/),cmd=t[0],args=[],opts={};
  for(const x of t.slice(1)){const i=x.indexOf('=');
    if(i>0)opts[x.slice(0,i)]=x.slice(i+1);else args.push(x);}
  window.dev(cmd,args,opts);document.getElementById('cmd').value='';}
document.getElementById('cmd').addEventListener('keydown',e=>{if(e.key==='Enter')runInput();});
// Refresh the live frame ~1/s (skips if a request is in flight).
let busy=false;
async function tick(){
  if(!busy){busy=true;
    try{const img=document.getElementById('frame');
      await fetch('/frame?t='+Date.now()).then(r=>r.ok?r.blob():null).then(b=>{if(b)img.src=URL.createObjectURL(b);});
    }catch(e){} busy=false;}
}
// On load: bring the game up if it isn't, then start refreshing.
(async()=>{
  try{const s=await fetch('/status').then(r=>r.json());
    if(!s.game_running){statusEl.textContent='starting game…';await fetch('/game/start',{method:'POST',body:'{}'});}
    statusEl.textContent='ready';
  }catch(e){statusEl.textContent='supervisor error';}
  setInterval(tick,1000); tick();
})();
</script></body></html>"""

# --- shared state -----------------------------------------------------------
_game_proc = None          # subprocess.Popen | None
_last_build = None         # dict | None


def _game_running():
    return _game_proc is not None and _game_proc.poll() is None


def _post_to_game(payload, timeout=16):
    """POST a command dict to the running game's dev listener."""
    data = json.dumps(payload).encode()
    req = urllib.request.Request(
        f"http://localhost:{GAME_PORT}/", data=data,
        headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read().decode())


def _wait_until_ready(timeout=40):
    """Poll the game's ping until it answers or we give up."""
    deadline = time.time() + timeout
    while time.time() < deadline:
        if not _game_running():
            return False, "game process exited during startup"
        try:
            resp = _post_to_game({"cmd": "ping"}, timeout=3)
            if resp.get("ok"):
                return True, "ready"
        except Exception:
            pass
        time.sleep(0.4)
    return False, "timed out waiting for game to become ready"


def start_game(windowed=False, map_name=None, resolution=DEFAULT_RESOLUTION):
    global _game_proc
    if _game_running():
        return {"ok": True, "result": "already running", "pid": _game_proc.pid}
    if not os.path.exists(EXE):
        return {"ok": False, "error": f"exe not found: {EXE} (build first)"}

    args = [EXE, "--devserver", str(GAME_PORT)]
    if not windowed:
        args.append("--headless")
    if resolution:
        args += ["--resolution", resolution]
    _game_proc = subprocess.Popen(args, cwd=os.path.dirname(EXE))

    ok, msg = _wait_until_ready()
    if not ok:
        return {"ok": False, "error": msg}

    result = {"ok": True, "result": "started", "pid": _game_proc.pid,
              "windowed": windowed, "resolution": resolution}
    if map_name:
        try:
            result["start_game"] = _post_to_game({"cmd": "start_game", "args": [map_name]})
        except Exception as e:
            result["start_game_error"] = str(e)
    return result


def stop_game():
    global _game_proc
    if not _game_running():
        _game_proc = None
        return {"ok": True, "result": "not running"}
    pid = _game_proc.pid
    _game_proc.terminate()
    try:
        _game_proc.wait(timeout=8)
    except subprocess.TimeoutExpired:
        _game_proc.kill()
        _game_proc.wait(timeout=8)
    _game_proc = None
    return {"ok": True, "result": "stopped", "pid": pid}


def build():
    """Game must be stopped first — it locks the exe."""
    global _last_build
    was_running = _game_running()
    if was_running:
        stop_game()
    proc = subprocess.run(
        ["dotnet", "build", PROJECT, "-v", "q", "-nologo"],
        cwd=REPO_ROOT, capture_output=True, text=True)
    lines = (proc.stdout or "").splitlines()
    errors = [l for l in lines if ": error " in l]
    _last_build = {
        "ok": proc.returncode == 0,
        "returncode": proc.returncode,
        "errors": errors[:50],
        "tail": lines[-8:],
        "was_running": was_running,
    }
    return _last_build


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a):  # silence per-request stderr spam
        pass

    def _send(self, obj, status=200):
        body = json.dumps(obj).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _read_json(self):
        n = int(self.headers.get("Content-Length", 0) or 0)
        if n == 0:
            return {}
        try:
            return json.loads(self.rfile.read(n).decode())
        except Exception:
            return {}

    def _send_bytes(self, data, content_type, status=200):
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        path = self.path.split("?", 1)[0].rstrip("/") or "/"
        if path == "/":
            self._send_bytes(DASHBOARD_HTML.encode(), "text/html; charset=utf-8")
        elif path == "/frame":
            # Trigger a fresh screenshot (blocks until the PNG is written), then
            # return the bytes. Lets the dashboard show a near-live game view.
            if not _game_running():
                self._send({"ok": False, "error": "game not running"}, 503)
                return
            try:
                # Full-res for the dashboard's live view (the browser scales to fit).
                _post_to_game({"cmd": "screenshot", "args": [LIVE_FRAME],
                               "opts": {"downsample_to": "full"}})
                with open(os.path.join(SCREENSHOT_DIR, LIVE_FRAME + ".png"), "rb") as f:
                    self._send_bytes(f.read(), "image/png")
            except Exception as e:
                self._send({"ok": False, "error": str(e)}, 500)
        elif path == "/status":
            self._send({
                "ok": True,
                "game_running": _game_running(),
                "pid": _game_proc.pid if _game_running() else None,
                "supervisor_port": SUPERVISOR_PORT,
                "game_port": GAME_PORT,
                "exe_exists": os.path.exists(EXE),
                "last_build": _last_build,
            })
        else:
            self._send({"ok": False, "error": f"unknown GET {self.path}"}, 404)

    def do_POST(self):
        path = self.path.rstrip("/") or "/"
        body = self._read_json()
        try:
            if path == "/game/start":
                self._send(start_game(body.get("windowed", False), body.get("map"),
                                      body.get("resolution", DEFAULT_RESOLUTION)))
            elif path == "/game/stop":
                self._send(stop_game())
            elif path == "/game/restart":
                stop_game()
                b = build() if body.get("build") else None
                if b and not b["ok"]:
                    self._send({"ok": False, "error": "build failed", "build": b})
                    return
                res = start_game(body.get("windowed", False), body.get("map"),
                                 body.get("resolution", DEFAULT_RESOLUTION))
                if b:
                    res["build"] = b
                self._send(res)
            elif path == "/build":
                self._send(build())
            elif path == "/cmd":
                if not _game_running():
                    self._send({"ok": False, "error": "game not running"}, 409)
                    return
                self._send(_post_to_game(body))
            else:
                self._send({"ok": False, "error": f"unknown POST {path}"}, 404)
        except Exception as e:
            self._send({"ok": False, "error": str(e)}, 500)


def main():
    srv = ThreadingHTTPServer(("localhost", SUPERVISOR_PORT), Handler)
    print(f"[devserver] supervisor on http://localhost:{SUPERVISOR_PORT}/  "
          f"(game port {GAME_PORT})", flush=True)
    print(f"[devserver] repo root: {REPO_ROOT}", flush=True)
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        if _game_running():
            stop_game()


if __name__ == "__main__":
    main()
