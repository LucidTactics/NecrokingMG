using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Necroking.Core;
using Necroking.Data;
using Necroking.Lib;
using Necroking.Scenario;

namespace Necroking;

public partial class Game1 {
   // `hdrbar` dev command: a plain world-anchored HDR rectangle drawn through the
   // same strip+bloom pipeline as beams (LightningRenderer.Draw) — the controlled
   // fixture for zoom/bloom testing: no flicker/jitter/style, constant world size.
   internal bool _devHdrBar;
   internal Vec2 _devHdrBarPos;
   internal float _devHdrBarLen = 8f;        // world units
   internal float _devHdrBarWidth = 0.15f;   // world units (thin: ~5px at zoom 32)
   internal float _devHdrBarIntensity = 10f;
   internal int _devHdrBarCount = 1;         // parallel bars (between-beam fill tests)
   internal float _devHdrBarGap = 0.5f;      // world units between bar centers

   // `zoomhud` dev command: on-screen zoom + bloom code-path readout (top-right)
   // so sweep/artifact screenshots carry the exact numbers.
   internal bool _devZoomHud;

   // Ring buffers behind the `perf` dev command — fed once per frame at the end of
   // GameRenderer.Draw. Frame = real wall-clock Draw-to-Draw interval (valid in both
   // fixed and variable timestep, unlike _rawDt under fixed-timestep catch-up).
   internal readonly double[] _perfFrameMs = new double[300];
   internal readonly double[] _perfSimMs = new double[300];
   internal readonly double[] _perfDrawMs = new double[300];
   internal readonly double[] _perfPresentMs = new double[300];
   internal long _perfFrames;
   internal readonly System.Diagnostics.Stopwatch _perfFrameSw = new();

   void ExecuteDevCommand(Necroking.Dev.DevCommand c) {
      try {
         switch (c.Cmd) {
            case "ping":
               c.Complete(Necroking.Dev.DevServer.Ok("pong"));
               break;

            // Nightfall-ported rogue jump: the necromancer leaps to a point, reusing
            // partial Standup/Fall animations rather than dedicated jump clips
            // (NightfallPorts/RogueJump.cs). Handy for eyeballing the leap via drive-game.
            //   window.dev('roguejump')              → jump 5u along current facing
            //   window.dev('roguejump',['8'])        → jump 8u along facing
            //   window.dev('roguejump',['12','7'])   → jump to world point (12,7)
            case "roguejump": {
               int ni = _sim.NecromancerIndex;
               if (ni < 0) { c.Complete(Necroking.Dev.DevServer.Error("no necromancer in sim")); break; }
               var mu = _sim.UnitsMut;
               var inv = System.Globalization.CultureInfo.InvariantCulture;
               var ns = System.Globalization.NumberStyles.Float;
               Vec2 dest;
               if (c.Args.Length >= 2
                   && float.TryParse(c.Args[0], ns, inv, out float wx)
                   && float.TryParse(c.Args[1], ns, inv, out float wy)) {
                  dest = new Vec2(wx, wy);
               } else {
                  float dist = 5f;
                  if (c.Args.Length >= 1) float.TryParse(c.Args[0], ns, inv, out dist);
                  dest = mu[ni].Position + Movement.FacingUtil.ForwardDir(mu[ni]) * dist;
               }
               Necroking.NightfallPorts.RogueJump.BeginJump(mu, ni, dest);
               c.Complete(Necroking.Dev.DevServer.Ok($"rogue jump unit#{ni} -> ({dest.X:F1},{dest.Y:F1})"));
               break;
            }

            // Toggle AI routine-transition tracing. Every transition (unit, archetype,
            // from → to routine, interrupt reason) is appended to log/ai_transition.log.
            //   window.dev('ai_trace',['on'|'off'])  → set;  no args → report current
            case "ai_trace": {
               if (c.Args.Length >= 1)
                  AI.AIControl.TraceTransitions = c.Args[0] is "on" or "true" or "1";
               c.Complete(Necroking.Dev.DevServer.Ok(
                  $"ai_trace = {(AI.AIControl.TraceTransitions ? "on" : "off")} (log/ai_transition.log)"));
               break;
            }

            // Memory diagnostic: managed heap before/after a forced compacting GC, plus
            // process private/working set. If managedAfter stays high across map reloads it's
            // a managed retained-reference leak; if managed stays flat but priv climbs it's a
            // GPU/unmanaged (texture) leak.
            case "mem": {
               long managedBefore = GC.GetTotalMemory(false);
               System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                   System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
               GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
               GC.WaitForPendingFinalizers();
               GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
               long managedAfter = GC.GetTotalMemory(true);
               using var proc = System.Diagnostics.Process.GetCurrentProcess();
               c.Complete(Necroking.Dev.DevServer.Ok(
                   $"managedBefore={managedBefore / (1024 * 1024)}MB " +
                   $"managedAfter={managedAfter / (1024 * 1024)}MB " +
                   $"priv={proc.PrivateMemorySize64 / (1024 * 1024)}MB " +
                   $"ws={proc.WorkingSet64 / (1024 * 1024)}MB"));
               break;
            }

            // Frame-time stats over the last ≤300 frames (rings fed in GameRenderer.Draw):
            // wall frame interval, sim tick, draw, present — avg/p50/p95/max each — plus
            // fps and cumulative GC collection counts.
            //   window.dev('perf')            → JSON stats
            //   window.dev('perf',['reset'])  → zero the ring (start a fresh window)
            case "perf": {
               if (c.Args.Length >= 1 && c.Args[0] == "reset") {
                  _perfFrames = 0;
                  c.Complete(Necroking.Dev.DevServer.Ok("perf ring reset"));
                  break;
               }
               int n = (int)Math.Min(_perfFrames, _perfFrameMs.Length);
               if (n == 0) {
                  c.Complete(Necroking.Dev.DevServer.Error("no frames recorded yet"));
                  break;
               }
               static string Stats(double[] ring, int n) {
                  var a = new double[n];
                  Array.Copy(ring, a, n);
                  Array.Sort(a);
                  double sum = 0;
                  for (int i = 0; i < n; i++) sum += a[i];
                  return FormattableString.Invariant(
                     $"{{\"avg\":{sum / n:F2},\"p50\":{a[n / 2]:F2},\"p95\":{a[(int)(n * 0.95)]:F2},\"max\":{a[n - 1]:F2}}}");
               }
               string frame = Stats(_perfFrameMs, n), sim = Stats(_perfSimMs, n);
               string draw = Stats(_perfDrawMs, n), present = Stats(_perfPresentMs, n);
               double fsum = 0;
               for (int i = 0; i < n; i++) fsum += _perfFrameMs[i];
               double fpsAvg = fsum > 0 ? 1000.0 * n / fsum : 0;
               string gc = FormattableString.Invariant(
                  $"{{\"gen0\":{GC.CollectionCount(0)},\"gen1\":{GC.CollectionCount(1)},\"gen2\":{GC.CollectionCount(2)}}}");
               c.Complete(Necroking.Dev.DevServer.OkRaw(FormattableString.Invariant(
                  $"{{\"frames\":{n},\"fps\":{fpsAvg:F1},\"frame\":{frame},\"sim\":{sim},\"draw\":{draw},\"present\":{present},\"gc\":{gc},\"units\":{(_sim?.Units.Count ?? 0)}}}")));
               break;
            }

            // Live census of every game-object collection: units by type/faction, env
            // objects by def, and the misc per-game collections. The counting lives in
            // GameSession.Census() so it's the single place to update as systems are added.
            case "census":
               c.Complete(Necroking.Dev.DevServer.Ok(_session.Census()));
               break;

            case "state":
               c.Complete(Necroking.Dev.DevServer.OkRaw(BuildDevStateJson()));
               break;

            // Live-edit a settings value by dotted path, no restart needed — for
            // dynamic settings the draw/sim reads every frame (e.g. tooltip toggles).
            //   window.dev('setting',['tooltips.showWorldHoverDebug','true'])  → set
            //   window.dev('setting',['tooltips.showWorldHoverDebug'])         → get
            // Path segments match either the JSON name or the C# property name
            // (case-insensitive). Persists to settings.json so it survives a restart.
            case "setting":
            case "set_setting": {
               if (c.Args.Length < 1) {
                  c.Complete(Necroking.Dev.DevServer.Error("setting needs: <dotted.path> [value]"));
                  break;
               }
               if (_gameData == null) {
                  c.Complete(Necroking.Dev.DevServer.Error("no game data loaded"));
                  break;
               }
               string path = c.Args[0];
               if (c.Args.Length < 2) {
                  // Getter.
                  if (TryGetSettingByPath(path, out object? cur, out string getErr))
                     c.Complete(Necroking.Dev.DevServer.Ok($"{path} = {cur ?? "null"}"));
                  else
                     c.Complete(Necroking.Dev.DevServer.Error(getErr));
                  break;
               }
               if (TrySetSettingByPath(path, c.Args[1], out string newVal, out string setErr)) {
                  // Persist so the change survives a restart, mirroring the Settings UI.
                  _gameData.Settings.Save(GamePaths.Resolve(GamePaths.UserSettingsJson));
                  c.Complete(Necroking.Dev.DevServer.Ok($"{path} = {newVal}"));
               } else {
                  c.Complete(Necroking.Dev.DevServer.Error(setErr));
               }
               break;
            }

            // Report the death-fog ("blight") density at a world point, plus its
            // fog-cell coords. window.dev('fog',[x,y])
            case "fog": {
               if (c.Args.Length < 2) {
                  c.Complete(Necroking.Dev.DevServer.Error("fog needs: <x> <y>"));
                  break;
               }
               float fx = DevFloat(c.Args[0]), fy = DevFloat(c.Args[1]);
               _deathFog.WorldToCell(fx, fy, out int fcx, out int fcy);
               float density = _deathFog.Sample(fx, fy);
               c.Complete(Necroking.Dev.DevServer.OkRaw(
                  $"{{\"x\":{fx},\"y\":{fy},\"cell\":[{fcx},{fcy}],\"density\":{density}}}"));
               break;
            }

            // Toggle / set the god-mode cheat buff on the necromancer (same as
            // Shift+P). window.dev('godmode',['on'|'off'|'toggle'])  (default toggle)
            case "godmode": {
               int ni = _sim.NecromancerIndex;
               if (ni < 0) { c.Complete(Necroking.Dev.DevServer.Error("no necromancer in world")); break; }
               bool has = Necroking.GameSystems.BuffSystem.HasBuff(_sim.Units, ni, "buff_god_mode");
               bool want = DevToggle(c.Args, has);
               if (want != has) ToggleGodMode(ni);
               c.Complete(Necroking.Dev.DevServer.Ok($"godmode {(want ? "on" : "off")}"));
               break;
            }

            // Toggle the HDR test bar — a plain world-anchored rectangle through the
            // beam strip+bloom pipeline, anchored at the current camera position.
            // count/gap draw parallel bars for between-beam bloom-fill tests.
            // devctl: cmd hdrbar on [len] [width] [intensity] [count] [gap] · cmd hdrbar off
            case "hdrbar": {
               bool want = DevToggle(c.Args, _devHdrBar);
               _devHdrBar = want;
               if (want)
               {
                  _devHdrBarPos = _camera.Position;
                  if (c.Args.Length > 1) _devHdrBarLen = DevFloat(c.Args[1]);
                  if (c.Args.Length > 2) _devHdrBarWidth = DevFloat(c.Args[2]);
                  if (c.Args.Length > 3) _devHdrBarIntensity = DevFloat(c.Args[3]);
                  if (c.Args.Length > 4) _devHdrBarCount = (int)DevFloat(c.Args[4]);
                  if (c.Args.Length > 5) _devHdrBarGap = DevFloat(c.Args[5]);
               }
               c.Complete(Necroking.Dev.DevServer.Ok(want
                  ? $"hdrbar on at ({_devHdrBarPos.X:F1},{_devHdrBarPos.Y:F1}) len={_devHdrBarLen} width={_devHdrBarWidth} intensity={_devHdrBarIntensity} count={_devHdrBarCount} gap={_devHdrBarGap}"
                  : "hdrbar off"));
               break;
            }

            // Toggle the on-screen zoom/bloom debug readout (zoom sweeps).
            // devctl: cmd zoomhud [on|off]
            case "zoomhud": {
               _devZoomHud = DevToggle(c.Args, _devZoomHud);
               c.Complete(Necroking.Dev.DevServer.Ok($"zoomhud {(_devZoomHud ? "on" : "off")}"));
               break;
            }

            // Set the bloom zoom-out dim exponent live (see BloomRenderer.DimPow).
            // devctl: cmd bloomdim 0.75 · argless reads the current value.
            case "bloomdim": {
               if (c.Args.Length > 0) _bloom.DimPow = DevFloat(c.Args[0]);
               c.Complete(Necroking.Dev.DevServer.Ok($"bloom DimPow = {_bloom.DimPow}"));
               break;
            }

            // Tweak a field on the ACTIVE weather preset in memory (never saved):
            // devctl: cmd weather rainAlpha 0.9 · cmd weather brightness 1 · argless lists fields.
            // For visual verification (rain under zoom, etc.) without editing weather.json.
            case "weather": {
               string presetId = _gameData.Settings.Weather.ActivePreset;
               var preset = _gameData.Weather.Get(presetId);
               if (preset == null) { c.Complete(Necroking.Dev.DevServer.Error($"no active weather preset ('{presetId}')")); break; }
               var props = typeof(Data.Registries.WeatherEffects).GetProperties();
               if (c.Args.Length < 2)
               {
                   var names = string.Join(", ", props.Select(p => p.Name));
                   c.Complete(Necroking.Dev.DevServer.Ok($"active={presetId}; fields: {names}"));
                   break;
               }
               var prop = props.FirstOrDefault(p => string.Equals(p.Name, c.Args[0], StringComparison.OrdinalIgnoreCase));
               if (prop == null) { c.Complete(Necroking.Dev.DevServer.Error($"unknown weather field: {c.Args[0]}")); break; }
               if (prop.PropertyType == typeof(float)) prop.SetValue(preset.Effects, DevFloat(c.Args[1]));
               else if (prop.PropertyType == typeof(bool)) prop.SetValue(preset.Effects, bool.Parse(c.Args[1]));
               else { c.Complete(Necroking.Dev.DevServer.Error($"unsupported field type {prop.PropertyType.Name}")); break; }
               c.Complete(Necroking.Dev.DevServer.Ok($"{presetId}.{prop.Name} = {c.Args[1]} (in-memory)"));
               break;
            }

            // Report the necromancer's spell cooldowns + the effective cooldown rate
            // (base × buff modifiers, e.g. ×10 under god mode). window.dev('cooldowns')
            case "cooldowns": {
               int ni = _sim.NecromancerIndex;
               if (ni < 0) { c.Complete(Necroking.Dev.DevServer.Error("no necromancer in world")); break; }
               float rate = Necroking.GameSystems.BuffSystem.GetModifiedExtra(_sim.Units, ni, "CooldownRate", _sim.NecroState.CooldownRate);
               var sb = new System.Text.StringBuilder();
               sb.Append($"{{\"rate\":{rate},\"base\":{_sim.NecroState.CooldownRate},\"cooldowns\":{{");
               bool first = true;
               foreach (var kv in _sim.NecroState.SpellCooldowns) {
                  if (kv.Value <= 0f) continue;
                  if (!first) sb.Append(',');
                  sb.Append($"\"{kv.Key}\":{kv.Value:F3}");
                  first = false;
               }
               sb.Append("}}");
               c.Complete(Necroking.Dev.DevServer.OkRaw(sb.ToString()));
               break;
            }

            case "spawn": {
               if (c.Args.Length < 3) {
                  c.Complete(Necroking.Dev.DevServer.Error("spawn needs: <type> <x> <y>"));
                  break;
               }

               if (!Enum.TryParse<UnitType>(c.Args[0], true, out var type)) {
                  c.Complete(Necroking.Dev.DevServer.Error($"unknown unit type: {c.Args[0]}"));
                  break;
               }

               float sx = DevFloat(c.Args[1]), sy = DevFloat(c.Args[2]);
               int idx = _sim.UnitsMut.AddUnit(new Vec2(sx, sy), type);
               c.Complete(Necroking.Dev.DevServer.Ok($"spawned {type} at ({sx},{sy}) idx={idx}"));
               break;
            }

            case "camera": {
               if (c.Args.Length < 2) {
                  c.Complete(Necroking.Dev.DevServer.Error("camera needs: <x> <y> [zoom]"));
                  break;
               }

               _camera.Position = new Vec2(DevFloat(c.Args[0]), DevFloat(c.Args[1]));
               // Clamp like the scroll-wheel path — a raw 0/negative zoom NaNs the
               // world transform until the next camera command.
               if (c.Args.Length >= 3)
                  _camera.Zoom = Math.Clamp(DevFloat(c.Args[2]), _camera.MinZoom, _camera.MaxZoom);
               // Detach from the necromancer so the set position actually sticks (otherwise the
               // follow snaps back next frame). Re-attach with 'free_camera off'. This is why dev
               // testing no longer needs to remove the necromancer (which triggered game-over).
               _devFreeCamera = true;
               c.Complete(Necroking.Dev.DevServer.Ok(
                  $"camera ({_camera.Position.X},{_camera.Position.Y}) zoom={_camera.Zoom} (free)"));
               break;
            }

            // Toggle camera-follow detachment without killing the necromancer.
            case "free_camera": {   // window.dev('free_camera',['off'])  | ['on'] | ['toggle'] | []
               _devFreeCamera = DevToggle(c.Args, _devFreeCamera);
               c.Complete(Necroking.Dev.DevServer.Ok($"free_camera={_devFreeCamera}" + (_devFreeCamera ? "" : " (following necromancer)")));
               break;
            }

            case "speed":
               if (c.Args.Length < 1) {
                  c.Complete(Necroking.Dev.DevServer.Error("speed needs: <n>"));
                  break;
               }

               _timeScale = DevFloat(c.Args[0]);
               _clock.ClearAllPauses();
               c.Complete(Necroking.Dev.DevServer.Ok($"speed={_timeScale}"));
               break;

            case "pause":
               _clock.Pause(GameClock.PauseSource.Dev);
               c.Complete(Necroking.Dev.DevServer.Ok("paused"));
               break;

            case "resume":
               _clock.ClearAllPauses();
               c.Complete(Necroking.Dev.DevServer.Ok("resumed"));
               break;

            // Load a map straight into gameplay. No arg → "empty_test": an empty,
            // grass-only map with a debug necromancer (all paths unlocked, +999
            // MaxMana) — the right starting point for testing technical behavior;
            // no map content to fight, all spells castable. See StartGame's
            // "empty_test" branch. Other maps: "default" (the real game map),
            // "testmap" (populated, normal necromancer).
            case "start_game": {
               string map = c.Args.Length > 0 ? c.Args[0] : "empty_test";
               StartGame(map);
               _menuState = MenuState.None;
               c.Complete(Necroking.Dev.DevServer.Ok($"started map={map} units={_sim.Units.Count}"));
               break;
            }

            // Start a coded scenario in the live session — same path as the scenario
            // menu / --scenario. Lets a driven session test scenario→scenario and
            // scenario→map transitions without a process restart.
            case "start_scenario": {
               if (c.Args.Length < 1) {
                  c.Complete(Necroking.Dev.DevServer.Error("start_scenario needs: <scenarioName>"));
                  break;
               }
               if (Scenario.ScenarioRegistry.Create(c.Args[0]) == null) {
                  c.Complete(Necroking.Dev.DevServer.Error($"unknown scenario '{c.Args[0]}'"));
                  break;
               }
               StartScenario(c.Args[0]);
               _menuState = MenuState.None;
               c.Complete(Necroking.Dev.DevServer.Ok($"started scenario={c.Args[0]} units={_sim.Units.Count}"));
               break;
            }

            // Press a main-menu button. Mirrors the click handlers in Update so
            // there's one definition of what each button does. Map loads go
            // through `start_game <map>` instead.
            case "menu": {
               if (c.Args.Length < 1) {
                  c.Complete(Necroking.Dev.DevServer.Error("menu needs: <new_game|scenarios|settings|main_menu|quit>"));
                  break;
               }

               switch (c.Args[0].ToLowerInvariant()) {
                  case "new_game":
                  case "play":
                     StartGame();
                     c.Complete(Necroking.Dev.DevServer.Ok(
                        $"new game, menuState={_menuState}, units={_sim.Units.Count}"));
                     break;
                  case "scenarios":
                     _menuState = MenuState.ScenarioList;
                     _scenarioScrollPx = 0f;
                     c.Complete(Necroking.Dev.DevServer.Ok("opened scenario list"));
                     break;
                  case "settings":
                     _menuState = MenuState.Settings;
                     c.Complete(Necroking.Dev.DevServer.Ok("opened settings"));
                     break;
                  case "main_menu":
                  case "back":
                     _menuState = MenuState.MainMenu;
                     _clock.ClearAllPauses();
                     _gameWorldLoaded = false;
                     c.Complete(Necroking.Dev.DevServer.Ok("returned to main menu"));
                     break;
                  case "quit":
                     c.Complete(Necroking.Dev.DevServer.Ok("quitting"));
                     Exit();
                     break;
                  default:
                     c.Complete(Necroking.Dev.DevServer.Error($"unknown menu button: {c.Args[0]}"));
                     break;
               }

               break;
            }

            // Render-pipeline pass control: `pass list`, `pass on <name>`,
            // `pass off <name>`. Free capability of the pass-list-as-data
            // renderer — bisect visual bugs by toggling passes live.
            case "pass": {
               string sub = c.Args.Length > 0 ? c.Args[0].ToLowerInvariant() : "list";
               if (sub == "list") {
                  c.Complete(Necroking.Dev.DevServer.Ok(_gameRenderer.DescribePipeline()));
                  break;
               }
               if ((sub == "on" || sub == "off") && c.Args.Length >= 2) {
                  string pname = c.Args[1];
                  if (_gameRenderer.TrySetPassEnabled(pname, sub == "on"))
                     c.Complete(Necroking.Dev.DevServer.Ok($"pass '{pname}' -> {sub}"));
                  else
                     c.Complete(Necroking.Dev.DevServer.Error($"no pass named '{pname}' (try: pass list)"));
                  break;
               }
               c.Complete(Necroking.Dev.DevServer.Error("pass needs: list | on <name> | off <name>"));
               break;
            }

            // Ground fog banks (depth-stamped wisp volume):
            //   groundfog add <x> <y> [radius] [density] [ttl]
            //   groundfog at_camera [radius] [density]
            //   groundfog clear
            case "groundfog": {
               string sub = c.Args.Length > 0 ? c.Args[0].ToLowerInvariant() : "";
               if (sub == "clear") {
                  _groundFog.Clear();
                  c.Complete(Necroking.Dev.DevServer.Ok("ground fog cleared"));
                  break;
               }
               if (sub == "add" && c.Args.Length >= 3) {
                  float gx = DevFloat(c.Args[1]), gy = DevFloat(c.Args[2]);
                  float radius = c.Args.Length > 3 ? DevFloat(c.Args[3]) : 12f;
                  float density = c.Args.Length > 4 ? DevFloat(c.Args[4]) : 0.7f;
                  float ttl = c.Args.Length > 5 ? DevFloat(c.Args[5]) : -1f;
                  _groundFog.SpawnBank(new Vec2(gx, gy), radius, density, ttl);
                  c.Complete(Necroking.Dev.DevServer.Ok($"fog bank at ({gx},{gy}) r={radius} d={density} banks={_groundFog.BankCount}"));
                  break;
               }
               if (sub == "at_camera") {
                  float radius = c.Args.Length > 1 ? DevFloat(c.Args[1]) : 12f;
                  float density = c.Args.Length > 2 ? DevFloat(c.Args[2]) : 0.7f;
                  _groundFog.SpawnBank(_camera.Position, radius, density);
                  c.Complete(Necroking.Dev.DevServer.Ok($"fog bank at camera ({_camera.Position.X:F0},{_camera.Position.Y:F0}) r={radius} d={density}"));
                  break;
               }
               c.Complete(Necroking.Dev.DevServer.Error("groundfog needs: add <x> <y> [radius] [density] [ttl] | at_camera [radius] [density] | clear"));
               break;
            }

            // List env objects as JSON: `objects [substring]` filters by def id
            // (e.g. `objects mush`); `objects foragable` lists foragables only.
            // Listing caps at 100 entries; `count` is the full match total so a
            // capped result is distinguishable from "exactly 100 objects".
            case "objects": {
               string filter = c.Args.Length > 0 ? c.Args[0].ToLowerInvariant() : "";
               bool foragableOnly = filter == "foragable";
               var sb = new System.Text.StringBuilder("[");
               int n = 0;
               for (int i = 0; i < _envSystem.ObjectCount; i++)
               {
                  if (!_envSystem.IsObjectVisible(i)) continue;
                  var obj = _envSystem.GetObject(i);
                  var def = _envSystem.GetDef(obj.DefIndex);
                  if (foragableOnly && !def.IsForagable) continue;
                  if (!foragableOnly && filter.Length > 0
                      && !def.Id.ToLowerInvariant().Contains(filter)) continue;
                  if (n < 100) {
                     if (n > 0) sb.Append(',');
                     sb.Append($"{{\"idx\":{i},\"def\":\"{def.Id}\",\"x\":{obj.X:F1},\"y\":{obj.Y:F1},\"foragable\":{(def.IsForagable ? "true" : "false")}}}");
                  }
                  n++;
               }
               sb.Append(']');
               c.Complete(Necroking.Dev.DevServer.OkRaw(
                  $"{{\"count\":{n},\"truncated\":{(n > 100 ? "true" : "false")},\"objects\":{sb}}}"));
               break;
            }

            // Necromancer inventory contents as JSON.
            case "inventory": {
               var sb = new System.Text.StringBuilder("[");
               bool first = true;
               for (int i = 0; i < _inventory.SlotCount; i++)
               {
                  var slot = _inventory.GetSlot(i);
                  if (slot.IsEmpty) continue;
                  if (!first) sb.Append(',');
                  sb.Append($"{{\"item\":\"{slot.ItemId}\",\"qty\":{slot.Quantity}}}");
                  first = false;
               }
               sb.Append(']');
               c.Complete(Necroking.Dev.DevServer.OkRaw(sb.ToString()));
               break;
            }

            case "screenshot": {
               string name = c.Opt("name") ?? (c.Args.Length > 0 ? c.Args[0] : "devshot");
               // The dashboard polls a "live" frame ~1/s. When the window is shown
               // on-screen the user is already watching the real game, so that
               // constant capture is wasted work (and a per-frame hitch) — skip it.
               // Explicit named shots (verification) still capture normally.
               if (_devWindowShown && name == "live") {
                  c.Complete(Necroking.Dev.DevServer.Ok("live frame suppressed (window shown)"));
                  break;
               }
               var (dw, dh) = ParseDownsample(c.Opt("downsample_to"));
               // Completed in Draw once the PNG is actually written.
               _pendingDevScreenshotCmd?.Complete(Necroking.Dev.DevServer.Error("superseded by newer screenshot"));
               _pendingDevScreenshot = name;
               _pendingDevScreenshotCmd = c;
               _devShotW = dw;
               _devShotH = dh;
               _devShotNoUi = c.OptBool("no_ui");
               _devShotNoGround = c.OptBool("no_ground");
               break;
            }

            // List every previewable UI panel, its tabs, and the in-game
            // overlays — discovery for the dev/preview workflow.
            case "panels": {
               var js = System.Text.Json.JsonSerializer.Serialize(new {
                  panels = new[] {
                     "main_menu", "scenarios", "load_menu", "game", "pause", "save_menu", "settings", "multiplayer",
                     "unit_editor", "spell_editor", "map_editor", "ui_editor", "item_editor"
                  },
                  tabs = new {
                     settings = Necroking.Editor.SettingsWindow.TabIds,
                     map_editor = Enum.GetNames(typeof(Necroking.Editor.MapEditorTab)),
                     ui_editor = Enum.GetNames(typeof(Necroking.Editor.UIEditorTab)),
                  },
                  overlays = new[] { "inventory", "character_stats", "skill_book", "grimoire", "character_sheet", "build_menu", "crafting_menu" },
                  current = _menuState.ToString(),
               });
               c.Complete(Necroking.Dev.DevServer.OkRaw(js));
               break;
            }

            // Dump the central UI hit-rect registry: every active UI region that
            // blocks mouse interaction with the world (popup panels, HUD buttons/
            // bars, toasts), plus where the cursor is relative to them. The
            // registry is rebuilt each Update (Game1.RebuildUIHitRects), so this
            // reflects the frame that just ran.
            case "ui_rects": {
               int uiMx = (int)_input.MousePos.X, uiMy = (int)_input.MousePos.Y;
               var js = System.Text.Json.JsonSerializer.Serialize(new {
                  mouse = new { x = uiMx, y = uiMy },
                  mouseOverUI = _input.MouseOverUI,
                  hitId = _uiHits.HitId(uiMx, uiMy),
                  rects = _uiHits.Entries.Select(e => new {
                     id = e.Id,
                     x = e.FullScreen || e.Probe != null ? (int?)null : e.Rect.X,
                     y = e.FullScreen || e.Probe != null ? (int?)null : e.Rect.Y,
                     w = e.FullScreen || e.Probe != null ? (int?)null : e.Rect.Width,
                     h = e.FullScreen || e.Probe != null ? (int?)null : e.Rect.Height,
                     fullscreen = e.FullScreen ? (bool?)true : null,
                     probe = e.Probe != null ? (bool?)true : null,
                     hit = e.Test(uiMx, uiMy),
                  }).ToArray(),
               }, new System.Text.Json.JsonSerializerOptions {
                  DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
               });
               c.Complete(Necroking.Dev.DevServer.OkRaw(js));
               break;
            }

            // Switch to a named UI panel; optional 2nd arg sets a tab on it.
            case "panel": {
               if (c.Args.Length < 1) {
                  c.Complete(Necroking.Dev.DevServer.Error("panel needs: <name> [tab] — run 'panels' for the list"));
                  break;
               }

               if (!SetUiPanel(c.Args[0])) {
                  c.Complete(Necroking.Dev.DevServer.Error($"unknown panel: {c.Args[0]} — run 'panels' for the list"));
                  break;
               }

               string tabNote = "";
               if (c.Args.Length >= 2)
                  tabNote = ApplyPanelTab(c.Args[1])
                     ? $", tab={c.Args[1]}"
                     : $", tab '{c.Args[1]}' not valid for {_menuState}";
               c.Complete(Necroking.Dev.DevServer.Ok($"panel={_menuState}{tabNote}"));
               break;
            }

            // Set the active tab on whatever panel is currently open.
            case "tab": {
               if (c.Args.Length < 1) {
                  c.Complete(Necroking.Dev.DevServer.Error("tab needs: <name> — run 'panels' for valid tabs"));
                  break;
               }

               if (!ApplyPanelTab(c.Args[0])) {
                  c.Complete(Necroking.Dev.DevServer.Error(
                     $"tab '{c.Args[0]}' not valid for {_menuState} (run 'panels')"));
                  break;
               }

               c.Complete(Necroking.Dev.DevServer.Ok($"panel={_menuState} tab={c.Args[0]}"));
               break;
            }

            // Set the map editor's active-tab list scroll (px). Headless stand-in
            // for the mouse wheel — lets screenshots verify mid-row scrolling.
            case "map_scroll": {
               if (c.Args.Length < 1) {
                  c.Complete(Necroking.Dev.DevServer.Error("map_scroll needs: <px>"));
                  break;
               }
               _mapEditor.DevSetScroll(DevFloat(c.Args[0]));
               c.Complete(Necroking.Dev.DevServer.Ok($"scroll={c.Args[0]}"));
               break;
            }

            // Show/hide an in-game overlay (inventory, stats, skill book, ...).
            case "overlay": {
               if (c.Args.Length < 1) {
                  c.Complete(Necroking.Dev.DevServer.Error(
                     "overlay needs: <name> [open|close|toggle] — run 'panels' for the list"));
                  break;
               }

               string action = c.Args.Length >= 2 ? c.Args[1].ToLowerInvariant() : "toggle";
               string[] extra = c.Args.Length > 2 ? c.Args[2..] : System.Array.Empty<string>();
               string? r = SetOverlay(c.Args[0], action, extra);
               if (r == null) {
                  c.Complete(Necroking.Dev.DevServer.Error(
                     $"unknown overlay: {c.Args[0]} — run 'panels' for the list"));
                  break;
               }

               c.Complete(Necroking.Dev.DevServer.Ok(r));
               break;
            }

            // Dump a unit's active buffs (id, remaining, permanent, stacks).
            case "buffs": {                        // devctl: cmd buffs [selector]
               var bIdxs = DevResolveUnits(c.Args.Length >= 1 ? c.Args[0] : "necro");
               if (bIdxs.Count == 0) {
                  c.Complete(Necroking.Dev.DevServer.Error("no units match"));
                  break;
               }
               var js = System.Text.Json.JsonSerializer.Serialize(bIdxs.Select(i => new {
                  idx = i,
                  def = _sim.Units[i].UnitDefID,
                  buffs = _sim.Units[i].ActiveBuffs.Select(b => new {
                     id = b.BuffDefID,
                     remaining = b.RemainingDuration,
                     permanent = b.Permanent,
                     stacks = b.StackCount,
                  }).ToArray(),
               }).ToArray());
               c.Complete(Necroking.Dev.DevServer.OkRaw(js));
               break;
            }

            // Write a save game (same path as the pause-menu Save dialog).
            case "save_game": {                    // devctl: cmd save_game [name]
               if (!_gameWorldLoaded) {
                  c.Complete(Necroking.Dev.DevServer.Error("no game loaded"));
                  break;
               }
               string saveName = c.Args.Length >= 1 ? SanitizeSaveName(c.Args[0]) : "";
               if (saveName == "") saveName = UniqueSaveName();
               c.Complete(WriteSaveGame(saveName)
                  ? Necroking.Dev.DevServer.Ok($"saved '{saveName}' -> {SaveFilePath(saveName)}")
                  : Necroking.Dev.DevServer.Error("save failed (see 'saves' log)"));
               break;
            }

            // Load a save game (same path as the main-menu Load Game list).
            case "load_game": {                    // devctl: cmd load_game <name>
               if (c.Args.Length < 1) {
                  var names = ListSaveGames().Select(s => s.Name);
                  c.Complete(Necroking.Dev.DevServer.Error(
                     $"load_game needs: <name> — existing: [{string.Join(", ", names)}]"));
                  break;
               }
               c.Complete(LoadSaveGame(c.Args[0])
                  ? Necroking.Dev.DevServer.Ok($"loaded '{c.Args[0]}' map={_currentMapName}")
                  : Necroking.Dev.DevServer.Error($"load failed for '{c.Args[0]}' (see 'saves' log)"));
               break;
            }

            // Toggle a core HUD menu through the real click path (ToggleCoreMenu),
            // including the per-side panel exclusivity — unlike `overlay`, which
            // sets panel state directly. Replies with every panel's visibility.
            case "toggle_menu": {
               if (c.Args.Length < 1) {
                  c.Complete(Necroking.Dev.DevServer.Error(
                     "toggle_menu needs: <inventory|crafting|building|grimoire|skills|character>"));
                  break;
               }

               int idx = c.Args[0].ToLowerInvariant() switch {
                  "inventory" => Necroking.UI.HUDRenderer.MenuInventory,
                  "crafting" => Necroking.UI.HUDRenderer.MenuCrafting,
                  "building" => Necroking.UI.HUDRenderer.MenuBuilding,
                  "grimoire" => Necroking.UI.HUDRenderer.MenuGrimoire,
                  "skills" or "skill_book" => Necroking.UI.HUDRenderer.MenuSkills,
                  "character" or "stats" => Necroking.UI.HUDRenderer.MenuCharacter,
                  _ => -1,
               };
               if (idx < 0) {
                  c.Complete(Necroking.Dev.DevServer.Error($"unknown menu: {c.Args[0]}"));
                  break;
               }

               if (!_gameWorldLoaded) StartGame();
               _gameRenderer.ToggleCoreMenu(idx, GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
               c.Complete(Necroking.Dev.DevServer.OkRaw(
                  $"{{\"inventory\":{(_inventoryUI.IsVisible ? "true" : "false")}," +
                  $"\"crafting\":{(_craftingMenu.IsVisible ? "true" : "false")}," +
                  $"\"building\":{(_buildingMenuUI.IsVisible ? "true" : "false")}," +
                  $"\"grimoire\":{(_grimoireOverlay.IsVisible ? "true" : "false")}," +
                  $"\"skills\":{(_skillBookOverlay.IsVisible ? "true" : "false")}," +
                  $"\"character\":{(_characterStatsUI.IsVisible ? "true" : "false")}," +
                  $"\"job_board\":{(_jobBoardUI.IsVisible ? "true" : "false")}," +
                  $"\"unit_info\":{(_unitInfoPanel.IsVisible ? "true" : "false")}}}"));
               break;
            }

            // Select an entry in the currently open editor (by index, def id,
            // or display name) so its preview/detail renders for a screenshot.
            case "select": {
               if (c.Args.Length < 1) {
                  c.Complete(
                     Necroking.Dev.DevServer.Error("select needs: <name|id|index> — open an editor panel first"));
                  break;
               }

               string? sel = SelectEditorEntry(string.Join(" ", c.Args));
               if (sel == null) {
                  c.Complete(Necroking.Dev.DevServer.Error(
                     $"nothing matched '{string.Join(" ", c.Args)}' in {_menuState} (open a unit/spell/item/ui editor first)"));
                  break;
               }

               c.Complete(Necroking.Dev.DevServer.Ok($"{_menuState} selected: {sel}"));
               break;
            }

            // Spawn a unit by its UnitDef id (full def stats/faction/AI), unlike
            // `spawn` which takes a bare UnitType. Optional 4th arg spawns a
            // small cluster (a line offset by 1 world unit on +x).
            case "spawn_def": {
               if (c.Args.Length < 3) {
                  c.Complete(Necroking.Dev.DevServer.Error("spawn_def needs: <unitID> <x> <y> [count]"));
                  break;
               }

               if (_gameData.Units.Get(c.Args[0]) == null) {
                  c.Complete(Necroking.Dev.DevServer.Error($"unknown unit def: {c.Args[0]}"));
                  break;
               }

               float bx = DevFloat(c.Args[1]), by = DevFloat(c.Args[2]);
               int count = c.Args.Length >= 4 ? (int)DevFloat(c.Args[3]) : 1;
               if (count < 1) count = 1;
               var idxs = new List<int>(count);
               for (int i = 0; i < count; i++) {
                  // Use the Game1 wrapper (not bare SpawnUnitByID) so the def's
                  // Archetype + handler OnSpawn are applied — dev spawns then match
                  // real map/game spawns (e.g. an animal's WolfPack/DeerHerd AI).
                  idxs.Add(SpawnUnit(c.Args[0], new Vec2(bx + i, by)));
               }
               c.Complete(Necroking.Dev.DevServer.OkRaw(
                  $"{{\"def\":{System.Text.Json.JsonSerializer.Serialize(c.Args[0])},\"count\":{count},\"indices\":[{string.Join(",", idxs)}]}}"));
               break;
            }

            // Spawn an undead def as a WILD undead (WildUndead archetype, not in the
            // horde) — same conversion the map-placed-unit loader applies, for testing
            // the wild→join flow without editing a map: spawn_wild <unitID> <x> <y>
            case "spawn_wild": {
               if (c.Args.Length < 3) {
                  c.Complete(Necroking.Dev.DevServer.Error("spawn_wild needs: <unitID> <x> <y>"));
                  break;
               }
               if (_gameData.Units.Get(c.Args[0]) == null) {
                  c.Complete(Necroking.Dev.DevServer.Error($"unknown unit def: {c.Args[0]}"));
                  break;
               }
               int wIdx = SpawnUnit(c.Args[0], new Vec2(DevFloat(c.Args[1]), DevFloat(c.Args[2])));
               if (wIdx < 0 || _sim.Units[wIdx].Faction != Faction.Undead) {
                  c.Complete(Necroking.Dev.DevServer.Error("spawn failed or def is not undead"));
                  break;
               }
               MakeUnitWild(wIdx);
               c.Complete(Necroking.Dev.DevServer.OkRaw(
                  $"{{\"def\":{System.Text.Json.JsonSerializer.Serialize(c.Args[0])},\"index\":{wIdx},\"id\":{_sim.Units[wIdx].Id}}}"));
               break;
            }

            // Add a runtime map zone with one spawn-table entry, for driving the periodic
            // zone spawn system without editor mouse work:
            // zone_add <kind> <x> <y> <halfW> <halfH> [defId] [perMinute] [maxAlive]
            case "zone_add": {
               if (c.Args.Length < 5) {
                  c.Complete(Necroking.Dev.DevServer.Error("zone_add needs: <kind> <x> <y> <halfW> <halfH> [defId] [perMinute] [maxAlive]"));
                  break;
               }
               if (!Enum.TryParse<GameSystems.ZoneKind>(c.Args[0], true, out var zkind)) {
                  c.Complete(Necroking.Dev.DevServer.Error($"unknown zone kind: {c.Args[0]}"));
                  break;
               }
               var devZone = new GameSystems.MapZone {
                  Id = $"devzone_{_zoneSystem.Count}",
                  Name = $"dev {zkind}",
                  Kind = zkind,
                  X = DevFloat(c.Args[1]), Y = DevFloat(c.Args[2]),
                  HalfW = DevFloat(c.Args[3]), HalfH = DevFloat(c.Args[4]),
               };
               if (c.Args.Length >= 6) {
                  devZone.Spawns.Add(new GameSystems.ZoneSpawnEntry {
                     DefId = c.Args[5],
                     PerMinute = c.Args.Length >= 7 ? DevFloat(c.Args[6]) : 1f,
                     MaxAlive = c.Args.Length >= 8 ? (int)DevFloat(c.Args[7]) : 5,
                  });
               }
               _zoneSystem.Add(devZone);
               FillZoneSpawnsAtStart(devZone, _sim.Grid); // same half-cap pre-fill as map load
               c.Complete(Necroking.Dev.DevServer.Ok($"added {devZone.Id} ({zkind}) at {devZone.X},{devZone.Y}"));
               break;
            }

            // ── Worker job system (P0/P1) dev verbs ──

            // Place an env object by def id: window.dev('place_obj',['mushroom_pile', x, y, scale])
            case "place_obj": {
               if (c.Args.Length < 3) { c.Complete(Necroking.Dev.DevServer.Error("place_obj needs: <defId> <x> <y> [scale]")); break; }
               int defIdx = _envSystem.FindDef(c.Args[0]);
               if (defIdx < 0) { c.Complete(Necroking.Dev.DevServer.Error($"unknown env def: {c.Args[0]}")); break; }
               float ox = DevFloat(c.Args[1]), oy = DevFloat(c.Args[2]);
               float oscale = c.Args.Length >= 4 ? DevFloat(c.Args[3]) : 1f;
               int oi = _envSystem.AddObject((ushort)defIdx, ox, oy, oscale);
               c.Complete(Necroking.Dev.DevServer.OkRaw($"{{\"objIdx\":{oi},\"def\":{System.Text.Json.JsonSerializer.Serialize(c.Args[0])}}}"));
               break;
            }

            // Drive the map editor's ProcGen brush headlessly: paint <style> at a
            // world point as if the brush were held for [seconds] (default 1).
            // window.dev('procgen_paint',['Forest','2100','1880','2'])
            case "procgen_paint": {
               if (c.Args.Length < 3) { c.Complete(Necroking.Dev.DevServer.Error("procgen_paint needs: <styleName> <x> <y> [seconds]")); break; }
               float ppx = DevFloat(c.Args[1]), ppy = DevFloat(c.Args[2]);
               float ppSecs = c.Args.Length >= 4 ? DevFloat(c.Args[3]) : 1f;
               c.Complete(Necroking.Dev.DevServer.Ok(
                  _mapEditor.DevPaintProcGen(c.Args[0], new Vec2(ppx, ppy), ppSecs)));
               break;
            }

            // Trigger the map editor's Save Map (writes assets/maps/<loaded map>.json
            // plus env_defs/procgen_styles). Optional arg saves under a different
            // filename without retargeting the editor. window.dev('save_map',['name'])
            case "save_map": {
               string? saveName = c.Args.Length > 0 ? c.Args[0] : null;
               c.Complete(Necroking.Dev.DevServer.Ok($"saved map '{_mapEditor.DevSaveMap(saveName)}'"));
               break;
            }

            // Press the map editor's "Reload Map" button: sets the same pending
            // flag the button click does; Game1.Update performs the world reload
            // next frame (requires the map editor to be open).
            case "reload_map": {
               if (_menuState != MenuState.MapEditor) {
                  c.Complete(Necroking.Dev.DevServer.Error("reload_map: map editor not open"));
                  break;
               }
               _mapEditor.PendingMapReload = true;
               c.Complete(Necroking.Dev.DevServer.Ok($"reload of '{_currentMapName}' requested"));
               break;
            }

            // Assign a unit to a grave (nearest unoccupied empty_grave if idx omitted):
            // window.dev('assign_worker',[unitId]) or window.dev('assign_worker',[unitId, graveObjIdx])
            case "assign_worker": {
               if (c.Args.Length < 1) { c.Complete(Necroking.Dev.DevServer.Error("assign_worker needs: <unitId> [graveObjIdx]")); break; }
               uint uid = (uint)DevFloat(c.Args[0]);
               int graveIdx;
               if (c.Args.Length >= 2) {
                  // Validate the explicit index like the auto-pick branch does.
                  graveIdx = (int)DevFloat(c.Args[1]);
                  if (graveIdx < 0 || graveIdx >= _envSystem.ObjectCount
                      || _envSystem.GetObject(graveIdx).DefIndex != _envSystem.FindDef("empty_grave")
                      || !_envSystem.GetObjectRuntime(graveIdx).Alive) {
                     c.Complete(Necroking.Dev.DevServer.Error($"graveObjIdx {graveIdx} is not a live empty_grave"));
                     break;
                  }
               }
               else {
                  graveIdx = -1;
                  int gdef = _envSystem.FindDef("empty_grave");
                  float bestSq = float.MaxValue;
                  Vec2 from = _sim.Units.TryGetIndex(uid, out int ui) ? _sim.Units[ui].Position : Vec2.Zero;
                  for (int i = 0; i < _envSystem.ObjectCount; i++) {
                     if (_envSystem.GetObject(i).DefIndex != gdef) continue;
                     if (!_envSystem.GetObjectRuntime(i).Alive) continue;
                     if (_workerSystem.IsGraveOccupied(i)) continue;
                     var o = _envSystem.GetObject(i);
                     float sq = (new Vec2(o.X, o.Y) - from).LengthSq();
                     if (sq < bestSq) { bestSq = sq; graveIdx = i; }
                  }
               }
               if (graveIdx < 0) { c.Complete(Necroking.Dev.DevServer.Error("no free empty_grave found")); break; }
               bool ok = _workerSystem.AssignWorker(uid, graveIdx);
               c.Complete(ok ? Necroking.Dev.DevServer.Ok($"assigned unit {uid} to grave {graveIdx}")
                              : Necroking.Dev.DevServer.Error($"could not assign unit {uid} (ineligible or grave taken)"));
               break;
            }

            case "unassign_worker": {
               if (c.Args.Length < 1) { c.Complete(Necroking.Dev.DevServer.Error("unassign_worker needs: <unitId>")); break; }
               bool ok = _workerSystem.UnassignWorker((uint)DevFloat(c.Args[0]));
               c.Complete(ok ? Necroking.Dev.DevServer.Ok("unassigned") : Necroking.Dev.DevServer.Error("not a worker"));
               break;
            }

            // Seed a building's stockpile: window.dev('stock_add',['alchemist_table','potion_poison','3'])
            case "stock_add": {
               if (c.Args.Length < 3) { c.Complete(Necroking.Dev.DevServer.Error("stock_add needs: <buildingDefId> <resource> <amount>")); break; }
               int di = _envSystem.FindDef(c.Args[0]);
               if (di < 0) { c.Complete(Necroking.Dev.DevServer.Error($"unknown def: {c.Args[0]}")); break; }
               int amt = (int)DevFloat(c.Args[2]); int added = 0, obj = -1;
               for (int i = 0; i < _envSystem.ObjectCount; i++)
                  if (_envSystem.GetObject(i).DefIndex == di && _envSystem.GetObjectRuntime(i).Alive)
                  { added = _workerSystem.Deposit(i, c.Args[1], amt); obj = i; break; }
               c.Complete(obj >= 0 ? Necroking.Dev.DevServer.Ok($"added {added} {c.Args[1]} to {c.Args[0]} obj{obj}")
                                   : Necroking.Dev.DevServer.Error($"no {c.Args[0]} placed"));
               break;
            }

            // Dump job + worker state: window.dev('jobs')
            case "jobs": {
               var sb = new System.Text.StringBuilder();
               foreach (var js in _workerSystem.Jobs) {
                  int derived = _workerSystem.DerivedMax(js.Def);
                  bool active = _workerSystem.IsJobActive(js);
                  sb.Append($"[{js.Priority}] {js.Def.Id} ({js.Def.Archetype}) bldg={js.Def.BuildingDefId} derivedMax={derived} cap={js.WorkerCap} active={active} assigned={js.AssignedWorkers.Count}\n");
               }
               sb.Append("workers:\n");
               for (int i = 0; i < _sim.Units.Count; i++) {
                  var u = _sim.Units[i];
                  if (u.Archetype != AI.ArchetypeRegistry.Worker) continue;
                  sb.Append($"  unit {u.Id} home={u.WorkerHomeObjIdx} job='{u.WorkerJobId}' phase={u.WorkerPhase} carry='{u.WorkerCarryType}'x{u.WorkerCarryAmount} pos=({u.Position.X:F1},{u.Position.Y:F1})\n");
               }
               sb.Append("stock:\n");
               sb.Append(_workerSystem.StockReport());
               c.Complete(Necroking.Dev.DevServer.Ok(sb.ToString()));
               break;
            }

            // Fire the Job Board's Auto-assign button logic directly: window.dev('auto_assign')
            case "auto_assign": {
               int n = _workerSystem.AutoAssignWorkers();
               c.Complete(Necroking.Dev.DevServer.Ok($"auto-assign: re-housed {n} undead, caps restored, dispatched"));
               break;
            }

            // Full economy scene: every building + sources + corpses + 6 assigned
            // workers. window.dev('worker_scene')
            case "worker_scene": {
               Vec2 nb = _sim.NecromancerIndex >= 0 ? _sim.Units[_sim.NecromancerIndex].Position : new Vec2(2096, 1882);
               int Def(string id) => _envSystem.FindDef(id);
               string[] req = { "empty_grave", "mushroom_pile", "corpse_pile", "poison_vat",
                  "harvesting_table", "necro_table", "alchemist_table", "deathcap", "BerryBush1Ber" };
               string missing = "";
               foreach (var r in req) if (Def(r) < 0) { missing = r; break; }
               if (missing != "") { c.Complete(Necroking.Dev.DevServer.Error($"missing env def: {missing}")); break; }
               // Guard the worker unit def too — SpawnUnit on a missing def would
               // silently add a default-stats Dynamic unit instead of failing.
               if (_gameData.Units.Get("skeleton") == null) { c.Complete(Necroking.Dev.DevServer.Error("missing unit def: skeleton")); break; }
               int Place(string id, float x, float y, float s = 1f) => _envSystem.AddObject((ushort)Def(id), x, y, s);

               // Buildings
               var graves = new List<int>();
               for (int g = 0; g < 6; g++)   // 3x2 grid, spaced wide enough that workers don't snag pathing between them
                  graves.Add(Place("empty_grave", nb.X - 8 + (g % 3) * 4.5f, nb.Y - 3 + (g / 3) * 4.5f));
               Place("mushroom_pile", nb.X + 6, nb.Y - 5);
               Place("corpse_pile", nb.X + 6, nb.Y);
               Place("poison_vat", nb.X + 6, nb.Y + 5);
               Place("harvesting_table", nb.X + 11, nb.Y - 3);
               Place("necro_table", nb.X + 11, nb.Y + 3);
               Place("alchemist_table", nb.X + 15, nb.Y);
               // Sources
               for (int m = 0; m < 10; m++)
                  Place("deathcap", nb.X + 19 + (m % 5) * 1.4f, nb.Y - 4 + (m / 5) * 2.5f);
               Place("BerryBush1Ber", nb.X + 1, nb.Y + 7);
               Place("BerryBush1Ber", nb.X + 3, nb.Y + 7);
               // Corpses
               for (int k = 0; k < 8; k++)
                  _sim.SpawnLooseCorpse(new Vec2(nb.X - 8 + (k % 4) * 1.5f, nb.Y + 4 + (k / 4) * 1.5f), "skeleton");
               // Workers
               int assigned = 0;
               for (int w = 0; w < graves.Count; w++)
               {
                  int widx = SpawnUnit("skeleton", new Vec2(nb.X - 8 + w * 1.5f, nb.Y + 4));   // spread out below the graves
                  if (_workerSystem.AssignWorker(_sim.Units[widx].Id, graves[w])) assigned++;
               }
               c.Complete(Necroking.Dev.DevServer.Ok($"scene built: {graves.Count} graves, 7 buildings, sources + 8 corpses, {assigned} workers assigned"));
               break;
            }

            // Surface / re-hide the headless game window at runtime (NO restart). The
            // headless window renders normally; it's just parked off-screen, borderless,
            // and stripped of its taskbar button. 'show' moves it on-screen, gives it a
            // border + taskbar button, and focuses it so the user can play; 'hide' parks
            // it again. window.dev('window',['show'|'hide'|'toggle'])
            case "window": {
               string mode = c.Args.Length >= 1 ? c.Args[0].ToLowerInvariant() : "toggle";
               if (mode == "toggle") mode = _devWindowShown ? "hide" : "show";
               if (mode == "show")
               {
                  Window.IsBorderless = false;            // give it a draggable title bar
                  Window.Position = new Microsoft.Xna.Framework.Point(80, 80); // on-screen, top-left-ish
                  Core.WindowChrome.RestoreToTaskbarAndFocus();
                  _devWindowShown = true;
                  _taskbarHidden = true;                  // keep the Update-loop auto-hide latched off
                  c.Complete(Necroking.Dev.DevServer.Ok("window shown on-screen at 80,80 (bordered + focused)"));
               }
               else if (mode == "hide")
               {
                  Window.IsBorderless = true;
                  Window.Position = new Microsoft.Xna.Framework.Point(-10000, -10000);
                  Core.WindowChrome.HideFromTaskbar();
                  _devWindowShown = false;
                  _taskbarHidden = true;
                  c.Complete(Necroking.Dev.DevServer.Ok("window hidden off-screen (headless look restored)"));
               }
               else c.Complete(Necroking.Dev.DevServer.Error("window needs: show | hide | toggle"));
               break;
            }

            // Open the job board UI. window.dev('ui_job_board')
            case "ui_job_board": {
               EnsureInventoryUIsInitialized();
               if (!_jobBoardUI.IsVisible)
                  _jobBoardUI.Toggle(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
               c.Complete(Necroking.Dev.DevServer.Ok($"job board visible={_jobBoardUI.IsVisible}"));
               break;
            }
            // Open the grave roster UI for a grave (nearest if omitted): window.dev('ui_grave_roster',[graveObjIdx])
            case "ui_grave_roster": {
               int gi = c.Args.Length >= 1 ? (int)DevFloat(c.Args[0]) : -1;
               if (gi < 0) {
                  int gdef = _envSystem.FindDef("empty_grave");
                  for (int i = 0; i < _envSystem.ObjectCount; i++)
                     if (_envSystem.GetObject(i).DefIndex == gdef && _envSystem.GetObjectRuntime(i).Alive) { gi = i; break; }
               }
               if (gi < 0) { c.Complete(Necroking.Dev.DevServer.Error("no empty_grave found")); break; }
               EnsureInventoryUIsInitialized();
               _graveRosterUI.OpenForGrave(gi, GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
               c.Complete(Necroking.Dev.DevServer.Ok($"grave roster opened for obj {gi}"));
               break;
            }

            // Spawn undead units enrolled in the necromancer's horde (HordeMinion
            // archetype + horde membership), unlike spawn_def which leaves them as
            // free AttackClosest units. Useful for exercising horde/command behavior.
            // window.dev('spawn_horde',['skeleton','2090','1878','12'])
            case "spawn_horde": {
               if (c.Args.Length < 3) {
                  c.Complete(Necroking.Dev.DevServer.Error("spawn_horde needs: <unitID> <x> <y> [count]"));
                  break;
               }
               if (_gameData.Units.Get(c.Args[0]) == null) {
                  c.Complete(Necroking.Dev.DevServer.Error($"unknown unit def: {c.Args[0]}"));
                  break;
               }
               float hx = DevFloat(c.Args[1]), hy = DevFloat(c.Args[2]);
               int hcount = c.Args.Length >= 4 ? (int)DevFloat(c.Args[3]) : 1;
               if (hcount < 1) hcount = 1;
               var hidxs = new List<int>(hcount);
               for (int i = 0; i < hcount; i++)
                  hidxs.Add(_sim.SpawnZombieMinion(c.Args[0], new Vec2(hx + (i % 4) * 1.2f, hy + (i / 4) * 1.2f)));
               c.Complete(Necroking.Dev.DevServer.OkRaw(
                  $"{{\"def\":{System.Text.Json.JsonSerializer.Serialize(c.Args[0])},\"count\":{hcount},\"indices\":[{string.Join(",", hidxs)}]}}"));
               break;
            }

            // List units matching a selector (default "all") as a JSON array.
            case "units": {
               string sel = c.Args.Length > 0 ? string.Join(" ", c.Args) : "all";
               var idxs = DevResolveUnits(sel);
               var sb = new System.Text.StringBuilder("[");
               for (int i = 0; i < idxs.Count; i++) {
                  if (i > 0) sb.Append(',');
                  sb.Append(DevUnitJson(idxs[i]));
               }

               sb.Append(']');
               c.Complete(Necroking.Dev.DevServer.OkRaw(
                  $"{{\"count\":{idxs.Count},\"units\":{sb}}}"));
               break;
            }

            // Dump the corpse list with the fields spell targeting filters on,
            // so a NoValidTarget can be diagnosed (range / consumed / zombie-type).
            case "corpses": {
               var ci = System.Globalization.CultureInfo.InvariantCulture;
               int nIdx = _sim.NecromancerIndex;
               Vec2 nPos = nIdx >= 0 ? _sim.Units[nIdx].Position : default;
               var sb = new System.Text.StringBuilder("[");
               for (int i = 0; i < _sim.Corpses.Count; i++) {
                  if (i > 0) sb.Append(',');
                  var cp = _sim.Corpses[i];
                  float d = nIdx >= 0 ? (cp.Position - nPos).Length() : -1f;
                  string zt = Necroking.Game.TableCraftingSystem.ResolveZombieUnitID(_gameData, cp.UnitDefID);
                  bool hasCad = _corpseAnims.TryGetValue(cp.CorpseID, out var cadDbg);
                  string canim = hasCad ? cadDbg.Ctrl.CurrentState.ToString() : "none";
                  bool hasStandup = hasCad && cadDbg.Ctrl.HasAnim(Necroking.Render.AnimState.Standup);
                  sb.Append('{')
                    .Append($"\"i\":{i},")
                    .Append($"\"def\":{System.Text.Json.JsonSerializer.Serialize(cp.UnitDefID)},")
                    .Append($"\"x\":{cp.Position.X.ToString("F1", ci)},")
                    .Append($"\"y\":{cp.Position.Y.ToString("F1", ci)},")
                    .Append($"\"dist\":{d.ToString("F1", ci)},")
                    .Append($"\"dissolving\":{(cp.Dissolving ? "true" : "false")},")
                    .Append($"\"consumed\":{(cp.ConsumedBySummon ? "true" : "false")},")
                    .Append($"\"reanim\":{cp.ReanimInstanceId},")
                    .Append($"\"anim\":{System.Text.Json.JsonSerializer.Serialize(canim)},")
                    .Append($"\"hasStandup\":{(hasStandup ? "true" : "false")},")
                    .Append($"\"dragged\":{(cp.DraggedByUnitID != GameConstants.InvalidUnit ? "true" : "false")},")
                    .Append($"\"bagged\":{(cp.BaggedByUnitID != GameConstants.InvalidUnit ? "true" : "false")},")
                    .Append($"\"zombieType\":{System.Text.Json.JsonSerializer.Serialize(zt)}")
                    .Append('}');
               }
               sb.Append(']');
               c.Complete(Necroking.Dev.DevServer.OkRaw(
                  $"{{\"count\":{_sim.Corpses.Count},\"necroX\":{nPos.X.ToString("F1", ci)},\"necroY\":{nPos.Y.ToString("F1", ci)},\"corpses\":{sb}}}"));
               break;
            }

            // Learn a skill via the free-learn path (runs its effect) and dump the
            // resulting primary spell bar — lets a test confirm e.g. that learning
            // monster_summoner grants reanimate_corpse onto the bar.
            case "learn_skill": {
               if (c.Args.Length < 1) {
                  c.Complete(Necroking.Dev.DevServer.Error("learn_skill needs: <skillId>"));
                  break;
               }
               string sid = c.Args[0];
               bool already = _skillBookState.IsLearned(sid);
               TryAutoLearn(sid, "Dev Learn");
               bool now = _skillBookState.IsLearned(sid);
               var sb = new System.Text.StringBuilder("[");
               var slots = _spellBarState.Slots;
               if (slots != null) {
                  for (int i = 0; i < slots.Length; i++) {
                     if (i > 0) sb.Append(',');
                     sb.Append(System.Text.Json.JsonSerializer.Serialize(slots[i].SpellID ?? ""));
                  }
               }
               sb.Append(']');
               c.Complete(Necroking.Dev.DevServer.OkRaw(
                  $"{{\"skill\":{System.Text.Json.JsonSerializer.Serialize(sid)},\"wasLearned\":{(already ? "true" : "false")},\"nowLearned\":{(now ? "true" : "false")},\"primaryBar\":{sb}}}"));
               break;
            }

            // Dump the construction gate: building ids unlocked via the skill
            // tree + what the build menu currently lists (post-filter, as of its
            // last Open) — lets a drive-game check verify the gate without UI
            // scraping.
            case "build_unlocks": {
               var bj = new System.Text.StringBuilder("{\"unlocked\":[");
               bool bf = true;
               foreach (var id in _skillBookState.UnlockedBuildings) {
                  if (!bf) bj.Append(',');
                  bf = false;
                  bj.Append(System.Text.Json.JsonSerializer.Serialize(id));
               }
               bj.Append("],\"menuVisible\":");
               bj.Append(_buildingMenuUI.IsVisible ? "true" : "false");
               bj.Append(",\"menu\":[");
               bf = true;
               foreach (int di in _buildingMenuUI.BuildableDefIndices) {
                  if (!bf) bj.Append(',');
                  bf = false;
                  bj.Append(System.Text.Json.JsonSerializer.Serialize(_envSystem.Defs[di].Id));
               }
               bj.Append($"],\"defCount\":{_envSystem.DefCount}}}");
               c.Complete(Necroking.Dev.DevServer.OkRaw(bj.ToString()));
               break;
            }

            // Dump the skill-book economy (skill-point pools + milestone-event
            // tallies) so drive-game checks can verify earn paths (e.g. monster
            // kills granting monstrology points) without UI scraping.
            case "skill_points": {
               var sbj = new System.Text.StringBuilder("{\"points\":{");
               bool first = true;
               foreach (var sp in Necroking.Game.SkillBookState.SKILL_POINT_TYPES) {
                  if (!first) sbj.Append(',');
                  first = false;
                  sbj.Append($"{System.Text.Json.JsonSerializer.Serialize(sp)}:{_skillBookState.GetSkillPoints(sp)}");
               }
               sbj.Append("},\"events\":{");
               first = true;
               foreach (var et in Necroking.Game.SkillBookState.EVENT_TYPES) {
                  if (!first) sbj.Append(',');
                  first = false;
                  sbj.Append($"{System.Text.Json.JsonSerializer.Serialize(et)}:{_skillBookState.Events.Get(et)}");
               }
               sbj.Append("}}");
               c.Complete(Necroking.Dev.DevServer.OkRaw(sbj.ToString()));
               break;
            }

            // Assign a spell to a primary-bar slot ('-' clears it) so tests can
            // stage the bar without touching user settings/spellbar.json.
            case "set_spell_slot": {
               if (c.Args.Length < 2) {
                  c.Complete(Necroking.Dev.DevServer.Error("set_spell_slot needs: <slot 0-9> <spellID|->"));
                  break;
               }
               if (!int.TryParse(c.Args[0], out int slot) || _spellBarState.Slots == null
                   || slot < 0 || slot >= _spellBarState.Slots.Length) {
                  c.Complete(Necroking.Dev.DevServer.Error($"bad slot '{c.Args[0]}'"));
                  break;
               }
               string spellId = c.Args[1] == "-" ? "" : c.Args[1];
               if (spellId != "" && spellId != "melee_gather" && _gameData.Spells.Get(spellId) == null) {
                  c.Complete(Necroking.Dev.DevServer.Error($"unknown spell '{spellId}'"));
                  break;
               }
               _spellBarState.Slots[slot].SpellID = spellId;
               c.Complete(Necroking.Dev.DevServer.Ok($"slot {slot} = '{spellId}'"));
               break;
            }

            // Arm/cancel the circle-targeting aim mode (SpellDef.TargetingMode ==
            // "Circle") as if the slot's spellbar key was pressed. The circle
            // follows the cursor (combine with 'mousepos'); 'click' confirms.
            // aim <slot 0-9> | aim clear
            case "aim": {
               if (c.Args.Length < 1) {
                  c.Complete(Necroking.Dev.DevServer.Error("aim needs: <slot 0-9>  (or 'clear')"));
                  break;
               }
               if (c.Args[0].Equals("clear", StringComparison.OrdinalIgnoreCase)) {
                  _aimingSlot = -1;
                  c.Complete(Necroking.Dev.DevServer.Ok("aim cleared"));
                  break;
               }
               if (!int.TryParse(c.Args[0], out int aimSlot) || _spellBarState.Slots == null
                   || aimSlot < 0 || aimSlot >= _spellBarState.Slots.Length) {
                  c.Complete(Necroking.Dev.DevServer.Error($"bad slot '{c.Args[0]}'"));
                  break;
               }
               _aimingSlot = aimSlot;
               var aimedDef = AimedSpell();   // validates; clears the slot if not Circle
               if (aimedDef == null) {
                  c.Complete(Necroking.Dev.DevServer.Error($"slot {aimSlot} has no Circle-targeting spell"));
                  break;
               }
               c.Complete(Necroking.Dev.DevServer.Ok($"aiming slot {aimSlot} ({aimedDef.Id})"));
               break;
            }

            // Add items to the player inventory (e.g. potions for consumable-spell
            // tests). give_item <itemID> [qty]
            case "give_item": {
               if (c.Args.Length < 1) {
                  c.Complete(Necroking.Dev.DevServer.Error("give_item needs: <itemID> [qty]"));
                  break;
               }
               string itemId = c.Args[0];
               if (_gameData.Items.Get(itemId) == null) {
                  c.Complete(Necroking.Dev.DevServer.Error($"unknown item '{itemId}'"));
                  break;
               }
               int qty = c.Args.Length > 1 ? (int)DevFloat(c.Args[1]) : 1;
               int added = _inventory.AddItem(itemId, qty);
               c.Complete(Necroking.Dev.DevServer.Ok(
                  $"added {added}/{qty} {itemId}, have {_inventory.GetItemCount(itemId)}"));
               break;
            }

            // Detailed dump of the first unit matching a selector.
            case "unit": {
               if (c.Args.Length < 1) {
                  c.Complete(Necroking.Dev.DevServer.Error("unit needs: <selector>"));
                  break;
               }

               var idxs = DevResolveUnits(string.Join(" ", c.Args));
               if (idxs.Count == 0) {
                  c.Complete(Necroking.Dev.DevServer.Error($"no unit matched '{string.Join(" ", c.Args)}'"));
                  break;
               }

               c.Complete(Necroking.Dev.DevServer.OkRaw(DevUnitJson(idxs[0])));
               break;
            }

            // Follow/locomotion state dump for diagnosing horde-follow issues:
            // effort intent + ramp, speed cap, intent vs actual velocity, slot
            // position/distance, horde state, stuck accrual.
            case "follow_debug": {
               var fidxs = DevResolveUnits(c.Args.Length > 0 ? string.Join(" ", c.Args) : "undead");
               if (fidxs.Count == 0) { c.Complete(Necroking.Dev.DevServer.Error("no unit matched")); break; }
               var ci3 = System.Globalization.CultureInfo.InvariantCulture;
               var sb = new System.Text.StringBuilder("[");
               for (int k = 0; k < fidxs.Count; k++)
               {
                  int ui = fidxs[k];
                  var u = _sim.Units[ui];
                  Vec2 slotPos = default;
                  bool hasSlot = _sim.Horde != null && _sim.Horde.GetTargetPosition(u.Id, out slotPos);
                  float slotDist = hasSlot ? (u.Position - slotPos).Length() : -1f;
                  string hordeState = "none";
                  if (_sim.Horde != null)
                     foreach (var hu in _sim.Horde.HordeUnits)
                        if (hu.UnitID == u.Id) { hordeState = hu.State.ToString(); break; }
                  if (k > 0) sb.Append(',');
                  sb.Append("{" +
                     $"\"def\":{System.Text.Json.JsonSerializer.Serialize(u.UnitDefID)}," +
                     $"\"routine\":{u.Routine},\"sub\":{u.Subroutine}," +
                     $"\"hordeState\":\"{hordeState}\"," +
                     $"\"effort\":\"{u.MoveEffort}\",\"effortMult\":{u.EffortMult.ToString("F2", ci3)}," +
                     $"\"maxSpeed\":{u.MaxSpeed.ToString("F2", ci3)}," +
                     $"\"pos\":[{u.Position.X.ToString("F2", ci3)},{u.Position.Y.ToString("F2", ci3)}]," +
                     $"\"prefVel\":[{u.PreferredVel.X.ToString("F2", ci3)},{u.PreferredVel.Y.ToString("F2", ci3)}]," +
                     $"\"vel\":[{u.Velocity.X.ToString("F2", ci3)},{u.Velocity.Y.ToString("F2", ci3)}]," +
                     $"\"slot\":[{slotPos.X.ToString("F2", ci3)},{slotPos.Y.ToString("F2", ci3)}]," +
                     $"\"slotDist\":{slotDist.ToString("F2", ci3)}," +
                     $"\"hasSlot\":{(hasSlot ? "true" : "false")}," +
                     $"\"stuckTime\":{u.StuckTime.ToString("F2", ci3)}," +
                     $"\"necroMoving\":{(_sim.Horde?.IsNecroMoving == true ? "true" : "false")}" +
                     "}");
               }
               sb.Append(']');
               c.Complete(Necroking.Dev.DevServer.OkRaw(sb.ToString()));
               break;
            }

            // Resolved locomotion profile (feet-lock vels, gait thresholds,
            // calibrated vs CS-derived defaults) + the def's overrides — for
            // diagnosing gait/playback.
            case "locomotion": case "loco": {
               var lidxs = DevResolveUnits(c.Args.Length > 0 ? string.Join(" ", c.Args) : "necro");
               if (lidxs.Count == 0) { c.Complete(Necroking.Dev.DevServer.Error("no unit matched")); break; }
               var ldef = _gameData.Units.Get(_sim.Units[lidxs[0]].UnitDefID);
               if (ldef == null) { c.Complete(Necroking.Dev.DevServer.Error("no def")); break; }
               var prof = Movement.LocomotionProfile.FromUnit(ldef);
               var ci2 = System.Globalization.CultureInfo.InvariantCulture;
               bool hasCal = ldef.SpriteData?.Calibration != null;
               string ov(float? v) => v.HasValue ? v.Value.ToString("F2", ci2) : "null";
               c.Complete(Necroking.Dev.DevServer.OkRaw("{" +
                  $"\"def\":{System.Text.Json.JsonSerializer.Serialize(ldef.Id)}," +
                  $"\"derivedDefaultVels\":{(hasCal ? "false" : "true")}," +
                  $"\"hasCalibration\":{(hasCal ? "true" : "false")}," +
                  $"\"isQuadruped\":{(ldef.IsQuadruped ? "true" : "false")}," +
                  $"\"combatSpeed\":{(ldef.Stats?.CombatSpeed ?? 0f).ToString("F2", ci2)}," +
                  $"\"animWalkVel\":{prof.AnimWalkVel.ToString("F2", ci2)}," +
                  $"\"animJogVel\":{prof.AnimJogVel.ToString("F2", ci2)}," +
                  $"\"animRunVel\":{prof.AnimRunVel.ToString("F2", ci2)}," +
                  $"\"jogThreshold\":{prof.JogThreshold.ToString("F2", ci2)}," +
                  $"\"runThreshold\":{prof.RunThreshold.ToString("F2", ci2)}," +
                  $"\"walkOverride\":{ov(ldef.AnimWalkVelOverride)}," +
                  $"\"jogOverride\":{ov(ldef.AnimJogVelOverride)}," +
                  $"\"runOverride\":{ov(ldef.AnimRunVelOverride)}," +
                  $"\"jogSpeedMult\":{ldef.JogSpeedMultiplier.ToString("F2", ci2)}," +
                  $"\"sprintSpeedMult\":{ldef.SprintSpeedMultiplier.ToString("F2", ci2)}" +
                  "}"));
               break;
            }

            // Recent combat-log entries (default last 20).
            case "combat_log": {
               int n = c.Args.Length > 0 ? (int)DevFloat(c.Args[0]) : 20;
               var e = _sim.CombatLog.Entries;
               int start = Math.Max(0, e.Count - n);
               var sb = new System.Text.StringBuilder("[");
               for (int i = start; i < e.Count; i++) {
                  if (i > start) sb.Append(',');
                  var en = e[i];
                  sb.Append('{')
                     .Append($"\"t\":{en.Timestamp.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)},")
                     .Append($"\"attacker\":{System.Text.Json.JsonSerializer.Serialize(en.AttackerName)},")
                     .Append($"\"defender\":{System.Text.Json.JsonSerializer.Serialize(en.DefenderName)},")
                     .Append($"\"outcome\":\"{en.Outcome}\",")
                     .Append($"\"weapon\":{System.Text.Json.JsonSerializer.Serialize(en.WeaponName)},")
                     .Append($"\"damage\":{en.NetDamage},")
                     .Append($"\"hitLoc\":\"{en.HitLocationName}\"")
                     .Append('}');
               }

               sb.Append(']');
               c.Complete(Necroking.Dev.DevServer.OkRaw(
                  $"{{\"total\":{e.Count},\"entries\":{sb}}}"));
               break;
            }

            // Deal armor-negating damage to every unit matching a selector.
            case "damage": {
               if (c.Args.Length < 2) {
                  c.Complete(Necroking.Dev.DevServer.Error("damage needs: <selector> <amount>"));
                  break;
               }

               int amount = (int)DevFloat(c.Args[c.Args.Length - 1]);
               var idxs = DevResolveUnits(string.Join(" ", c.Args, 0, c.Args.Length - 1));
               foreach (int i in idxs) _sim.DealDamage(i, amount);
               c.Complete(Necroking.Dev.DevServer.Ok($"dealt {amount} to {idxs.Count} unit(s)"));
               break;
            }

            // Kill every unit matching a selector (massive armor-negating hit).
            case "kill": {
               if (c.Args.Length < 1) {
                  c.Complete(Necroking.Dev.DevServer.Error("kill needs: <selector>"));
                  break;
               }

               var idxs = DevResolveUnits(string.Join(" ", c.Args));
               foreach (int i in idxs) _sim.DealDamage(i, 999999);
               c.Complete(Necroking.Dev.DevServer.Ok($"killed {idxs.Count} unit(s)"));
               break;
            }

            // Knock units back with a physics impulse (InPhysics launch) — e.g. to
            // test channel/cast interrupts. Direction is +X unless dx/dy opts given.
            case "impulse": {   // window.dev('impulse',['necro','20','8'])
               if (c.Args.Length < 2) {
                  c.Complete(Necroking.Dev.DevServer.Error("impulse needs: <selector> <force> [up]"));
                  break;
               }
               float impUp = c.Args.Length >= 3 ? DevFloat(c.Args[c.Args.Length - 1]) : 4f;
               int forceArg = c.Args.Length >= 3 ? c.Args.Length - 2 : c.Args.Length - 1;
               float impForce = DevFloat(c.Args[forceArg]);
               var impDir = new Vec2(
                  c.Opt("dx") is string sdx ? DevFloat(sdx) : 1f,
                  c.Opt("dy") is string sdy ? DevFloat(sdy) : 0f);
               var impIdxs = DevResolveUnits(string.Join(" ", c.Args, 0, forceArg));
               int impN = 0;
               foreach (int i in impIdxs)
                  if (_sim.Physics.ApplyImpulse(_sim.UnitsMut, i, impDir, impForce, impUp)) impN++;
               c.Complete(Necroking.Dev.DevServer.Ok($"launched {impN}/{impIdxs.Count} unit(s)"));
               break;
            }

            // Flag units so dying reanimates them — exercises the on-death composite reanim path.
            case "zombify": {   // window.dev('zombify',['human']) then kill -> full reanim effect
               var zidxs = DevResolveUnits(c.Args.Length > 0 ? string.Join(" ", c.Args) : "all");
               foreach (int i in zidxs) _sim.UnitsMut[i].ZombieOnDeath = true;
               c.Complete(Necroking.Dev.DevServer.Ok($"zombified {zidxs.Count} unit(s)"));
               break;
            }

            // Corpse-less composite reanim (the table-craft path): a green cloud builds at (x,y) and a
            // zombie rises from it — no world body to morph.
            case "reanim_at": {   // window.dev('reanim_at',[x,y,'skeleton',riseSpeed,fogSpeed])
               if (c.Args.Length < 2) { c.Complete(Necroking.Dev.DevServer.Error("reanim_at needs: <x> <y> [defId] [riseSpeed] [fogSpeed]")); break; }
               string rdef = c.Args.Length > 2 ? c.Args[2] : "skeleton";
               float rspeed = c.Args.Length > 3 ? DevFloat(c.Args[3]) : 1f;
               float fspeed = c.Args.Length > 4 ? DevFloat(c.Args[4]) : 1f;
               QueueReanimRise(rdef, -1, "",   // "" → the raised unit's own effect (else reanim_smoke)
                  posOverride: new Vec2(DevFloat(c.Args[0]), DevFloat(c.Args[1])), facingOverride: 90f, scaleOverride: 1f,
                  riseSpeed: rspeed, fogSpeed: fspeed);
               c.Complete(Necroking.Dev.DevServer.Ok($"queued corpse-less reanim of '{rdef}' riseSpeed={rspeed} fogSpeed={fspeed}"));
               break;
            }

            // ScatterGlow (world-space light-scatter halos): toggle, global strength,
            // or live mist shaping while values are dialed in.
            case "scatterglow": {   // ['on'|'off'|'toggle'] | ['strength', v] | ['mist', gamma, floor, [knee]]
               var sperf = _gameData.Settings.Performance;
               if (c.Args.Length >= 2 && c.Args[0] == "strength")
                  sperf.ScatterGlowStrength = DevFloat(c.Args[1]);
               else if (c.Args.Length >= 3 && c.Args[0] == "mist")
               {
                  _scatterGlow.MistGamma = DevFloat(c.Args[1]);
                  _scatterGlow.MistFloor = DevFloat(c.Args[2]);
                  if (c.Args.Length >= 4) _scatterGlow.MistKnee = DevFloat(c.Args[3]);
               }
               else
                  sperf.ScatterGlow = DevToggle(c.Args, sperf.ScatterGlow);
               c.Complete(Necroking.Dev.DevServer.Ok(
                  $"scatterGlow={sperf.ScatterGlow} strength={sperf.ScatterGlowStrength:F2} " +
                  $"mist(gamma={_scatterGlow.MistGamma:F2},floor={_scatterGlow.MistFloor:F2},knee={_scatterGlow.MistKnee:F2}) " +
                  $"halos={_scatterGlow.LastHaloCount} dropped={_scatterGlow.LastDroppedCount}"));
               break;
            }

            // A/B the depth-sorted reanimation fog (same as the in-game 'H' key).
            case "depthfog": {   // window.dev('depthfog',['on'|'off'|'toggle'])
               var perf = _gameData.Settings.Performance;
               perf.DepthSortedFog = DevToggle(c.Args, perf.DepthSortedFog);
               _depthFogToastTimer = 2.25f;   // flash the on-screen state (same as the 'H' key)
               c.Complete(Necroking.Dev.DevServer.Ok($"depthSortedFog={perf.DepthSortedFog}"));
               break;
            }

            // Reanimate the nearest eligible corpse into a SPECIFIED zombie type (exercises the
            // cross-type standup morph). window.dev('reanim_into',['ZombieWolf'])  or  [...,x,y].
            case "reanim_into": {
               if (c.Args.Length < 1) { c.Complete(Necroking.Dev.DevServer.Error("reanim_into needs <zombieDefId> [x] [y]")); break; }
               string zinto = c.Args[0];
               float rix, riy;
               if (c.Args.Length >= 3) { rix = DevFloat(c.Args[1]); riy = DevFloat(c.Args[2]); }
               else { int ni = _sim.NecromancerIndex; var np = ni >= 0 ? _sim.Units[ni].Position : new Vec2(32f, 32f); rix = np.X; riy = np.Y; }
               int rbest = -1; float rbestD = float.MaxValue;
               for (int i = 0; i < _sim.Corpses.Count; i++)
               {
                  var cp = _sim.Corpses[i];
                  if (cp.Dissolving || cp.ConsumedBySummon) continue;
                  if (cp.DraggedByUnitID != GameConstants.InvalidUnit || cp.BaggedByUnitID != GameConstants.InvalidUnit) continue;
                  float dx = cp.Position.X - rix, dy = cp.Position.Y - riy; float d = dx * dx + dy * dy;
                  if (d < rbestD) { rbestD = d; rbest = i; }
               }
               if (rbest < 0) { c.Complete(Necroking.Dev.DevServer.Error("reanim_into: no eligible corpse")); break; }
               var rc = _sim.Corpses[rbest];
               QueueReanimRise(zinto, rc.CorpseID, "reanim_smoke");
               c.Complete(Necroking.Dev.DevServer.Ok($"reanimating corpse '{rc.UnitDefID}' (#{rc.CorpseID}) into '{zinto}'"));
               break;
            }

            // Pin the hover-highlight onto a unit (headless test has no real mouse) + force it on.
            case "hover": {   // window.dev('hover',['necro'])  |  window.dev('hover',['clear'])
               if (c.Args.Length >= 1 && c.Args[0].Equals("clear", System.StringComparison.OrdinalIgnoreCase))
               {
                  _devForceHoverUnitId = uint.MaxValue;
                  c.Complete(Necroking.Dev.DevServer.Ok("hover cleared"));
                  break;
               }
               var hvidx = DevResolveUnits(c.Args.Length > 0 ? string.Join(" ", c.Args) : "necro");
               if (hvidx.Count == 0) { c.Complete(Necroking.Dev.DevServer.Error("hover: no unit matched")); break; }
               _devForceHoverUnitId = _sim.Units[hvidx[0]].Id;
               c.Complete(Necroking.Dev.DevServer.Ok($"hovering unit id={_devForceHoverUnitId} (variant {_hoverHighlightVariant})"));
               break;
            }

            // Force-hover an env object by index (headless variant testing — no real mouse).
            case "hover_obj": {   // window.dev('hover_obj',[3])  |  window.dev('hover_obj',['clear'])
               if (c.Args.Length >= 1 && c.Args[0].Equals("clear", System.StringComparison.OrdinalIgnoreCase))
               {
                  _devForceHoverObjectIdx = -1;
                  c.Complete(Necroking.Dev.DevServer.Ok("hover_obj cleared"));
                  break;
               }
               if (c.Args.Length < 1) { c.Complete(Necroking.Dev.DevServer.Error("hover_obj needs <index|clear>")); break; }
               int oidx = (int)DevFloat(c.Args[0]);
               if (oidx < 0 || oidx >= _envSystem.ObjectCount) { c.Complete(Necroking.Dev.DevServer.Error($"hover_obj: index {oidx} out of range (0..{_envSystem.ObjectCount - 1})")); break; }
               _devForceHoverObjectIdx = oidx;
               var hod = _envSystem.Defs[_envSystem.GetObject(oidx).DefIndex];
               c.Complete(Necroking.Dev.DevServer.Ok($"hovering object idx={oidx} ({hod.Id}) (variant {_hoverHighlightVariant})"));
               break;
            }

            // Run the real object-pick hit-test at a SCREEN point (headless has no live mouse) —
            // verifies the building diamond/footprint hover area. Returns the picked object + def id.
            case "hover_at": {   // window.dev('hover_at',[640,360])
               if (c.Args.Length < 2) { c.Complete(Necroking.Dev.DevServer.Error("hover_at needs <screenX> <screenY>")); break; }
               var pt = new Microsoft.Xna.Framework.Vector2(DevFloat(c.Args[0]), DevFloat(c.Args[1]));
               int sw = GraphicsDevice.Viewport.Width, sh = GraphicsDevice.Viewport.Height;
               var cw = _camera.ScreenToWorld(pt, sw, sh);
               // Mirror the live pick's gating: the map editor inspects every env object.
               int idx = _gameRenderer.PickHoveredObject(pt, cw, _menuState == MenuState.MapEditor);
               string did = idx >= 0 ? _envSystem.Defs[_envSystem.GetObject(idx).DefIndex].Id : "(none)";
               c.Complete(Necroking.Dev.DevServer.OkRaw($"{{\"objIdx\":{idx},\"def\":{System.Text.Json.JsonSerializer.Serialize(did)},\"worldX\":{cw.X:F2},\"worldY\":{cw.Y:F2}}}"));
               break;
            }

            // Set the hover-highlight dev override (-1 = use Tooltips settings, 0-19 = force a
            // variant on everything, 20 = highlight off).
            case "hover_variant": {   // window.dev('hover_variant',[5])
               if (c.Args.Length < 1) { c.Complete(Necroking.Dev.DevServer.Error("hover_variant needs <-1..20>")); break; }
               _hoverHighlightVariant = System.Math.Clamp((int)DevFloat(c.Args[0]), -1, 20);
               _hoverVariantLabelTimer = 2.75f;
               c.Complete(Necroking.Dev.DevServer.Ok($"hover_variant={_hoverHighlightVariant}"));
               break;
            }

            // Remove every unit matching a selector outright (no corpse).
            case "remove": {
               if (c.Args.Length < 1) {
                  c.Complete(Necroking.Dev.DevServer.Error("remove needs: <selector>"));
                  break;
               }

               var idxs = DevResolveUnits(string.Join(" ", c.Args));
               idxs.Sort(); // remove high indices first (swap-and-pop shifts the tail)
               for (int k = idxs.Count - 1; k >= 0; k--) _sim.RemoveUnitTracked(idxs[k]);
               c.Complete(Necroking.Dev.DevServer.Ok($"removed {idxs.Count} unit(s)"));
               break;
            }

            // Mark matching units with a persistent white outline box (independent
            // of mouse hover / the ShowHoverHighlight setting) — for screenshots
            // that point at a specific unit. `mark clear` removes all marks.
            case "mark": {
               if (c.Args.Length >= 1 && c.Args[0].Equals("clear", StringComparison.OrdinalIgnoreCase)) {
                  _devMarkedUnitIds.Clear();
                  c.Complete(Necroking.Dev.DevServer.Ok("cleared all marks"));
                  break;
               }
               if (c.Args.Length < 1) {
                  c.Complete(Necroking.Dev.DevServer.Error("mark needs: <selector> | clear"));
                  break;
               }
               var idxs = DevResolveUnits(string.Join(" ", c.Args));
               foreach (int i in idxs) _devMarkedUnitIds.Add(_sim.Units[i].Id);
               c.Complete(Necroking.Dev.DevServer.Ok($"marked {idxs.Count} unit(s); {_devMarkedUnitIds.Count} total"));
               break;
            }

            // Remove marks: all (no args) or just those matching a selector.
            case "unmark": {
               if (c.Args.Length < 1) {
                  _devMarkedUnitIds.Clear();
                  c.Complete(Necroking.Dev.DevServer.Ok("cleared all marks"));
                  break;
               }
               var idxs = DevResolveUnits(string.Join(" ", c.Args));
               int removed = 0;
               foreach (int i in idxs) if (_devMarkedUnitIds.Remove(_sim.Units[i].Id)) removed++;
               c.Complete(Necroking.Dev.DevServer.Ok($"unmarked {removed}; {_devMarkedUnitIds.Count} remain"));
               break;
            }

            // Set the AI behaviour on every unit matching a selector.
            case "set_ai": {
               if (c.Args.Length < 2) {
                  c.Complete(Necroking.Dev.DevServer.Error("set_ai needs: <selector> <AIBehavior>"));
                  break;
               }

               if (!Enum.TryParse<AIBehavior>(c.Args[c.Args.Length - 1], true, out var ai)) {
                  c.Complete(Necroking.Dev.DevServer.Error(
                     $"unknown AIBehavior: {c.Args[c.Args.Length - 1]} (e.g. {string.Join(", ", Enum.GetNames(typeof(AIBehavior)))})"));
                  break;
               }

               var idxs = DevResolveUnits(string.Join(" ", c.Args, 0, c.Args.Length - 1));
               foreach (int i in idxs) _sim.UnitsMut[i].AI = ai;
               c.Complete(Necroking.Dev.DevServer.Ok($"set AI={ai} on {idxs.Count} unit(s)"));
               break;
            }

            // Order matching units to move to a world point (AI = MoveToPoint).
            case "move": {
               if (c.Args.Length < 3) {
                  c.Complete(Necroking.Dev.DevServer.Error("move needs: <selector> <x> <y>"));
                  break;
               }

               float mx = DevFloat(c.Args[c.Args.Length - 2]), my = DevFloat(c.Args[c.Args.Length - 1]);
               var idxs = DevResolveUnits(string.Join(" ", c.Args, 0, c.Args.Length - 2));
               foreach (int i in idxs) {
                  _sim.UnitsMut[i].MoveTarget = new Vec2(mx, my);
                  _sim.UnitsMut[i].AI = AIBehavior.MoveToPoint;
                  _sim.UnitsMut[i].Target = CombatTarget.None;
               }

               c.Complete(Necroking.Dev.DevServer.Ok($"moved {idxs.Count} unit(s) to ({mx},{my})"));
               break;
            }

            // Walk the (player-controlled) necromancer to a world point using the
            // same movement input WASD drives. Auto-cancels the instant any WASD
            // key is pressed (see the player-input block in Update), so manual
            // control always overrides it. 'clear'/'cancel'/'stop' aborts the walk.
            case "walk_necro": {
               if (_sim.NecromancerIndex < 0) {
                  c.Complete(Necroking.Dev.DevServer.Error("no necromancer in the sim"));
                  break;
               }
               if (c.Args.Length == 1 &&
                   (c.Args[0].Equals("clear", StringComparison.OrdinalIgnoreCase) ||
                    c.Args[0].Equals("cancel", StringComparison.OrdinalIgnoreCase) ||
                    c.Args[0].Equals("stop", StringComparison.OrdinalIgnoreCase))) {
                  _devWalkTarget = null;
                  _devWalkSprint = false;
                  c.Complete(Necroking.Dev.DevServer.Ok("walk_necro cancelled"));
                  break;
               }
               if (c.Args.Length < 2) {
                  c.Complete(Necroking.Dev.DevServer.Error("walk_necro needs: <x> <y> [sprint=true]  (or 'clear')"));
                  break;
               }
               float wx = DevFloat(c.Args[0]), wy = DevFloat(c.Args[1]);
               _devWalkTarget = new Vec2(wx, wy);
               _devWalkSprint = c.OptBool("sprint");
               c.Complete(Necroking.Dev.DevServer.Ok(
                  $"necromancer walking to ({wx},{wy}){(_devWalkSprint ? " at sprint" : "")}"));
               break;
            }

            // Override the necromancer's horde caps so summon/reanimate spells can
            // be exercised on the debug necromancer (whose caps are otherwise 0/1).
            case "set_cap": {   // set_cap <monster> [human]
               if (c.Args.Length < 1) {
                  c.Complete(Necroking.Dev.DevServer.Error("set_cap needs: <monsterCap> [humanCap]"));
                  break;
               }
               _sim.NecroState.MonsterCap = (int)DevFloat(c.Args[0]);
               if (c.Args.Length >= 2) _sim.NecroState.HumanCap = (int)DevFloat(c.Args[1]);
               c.Complete(Necroking.Dev.DevServer.Ok(
                  $"caps: monster={_sim.NecroState.MonsterCap} human={_sim.NecroState.HumanCap}"));
               break;
            }

            // Cast-plant inspector (todos/player_cast_plant.md): the per-frame
            // truth needed to verify the brake/gate/release cycle headlessly.
            case "cast_state": {
               int ni = _sim.NecromancerIndex;
               if (ni < 0) { c.Complete(Necroking.Dev.DevServer.Error("no necromancer")); break; }
               string pending = "null", anim = "null";
               bool waiting = false;
               if (_pendingCastAnim != null) {
                  pending = $"\"{_pendingCastAnim.Value.SpellID}\"";
                  waiting = _pendingCastAnim.Value.WaitingForPlant;
               }
               if (_unitAnims.TryGetValue(_sim.Units[ni].Id, out var na))
                  anim = $"\"{na.Ctrl.CurrentState}\"";
               var u = _sim.Units[ni];
               var ci = System.Globalization.CultureInfo.InvariantCulture;
               c.Complete(Necroking.Dev.DevServer.OkRaw(
                  $"{{\"castPlant\":{(_sim.NecroCastPlant ? "true" : "false")}," +
                  $"\"waitingForPlant\":{(waiting ? "true" : "false")}," +
                  $"\"pendingSpell\":{pending},\"animState\":{anim}," +
                  $"\"speed\":{u.Velocity.Length().ToString("F2", ci)}," +
                  $"\"facing\":{u.FacingAngle.ToString("F1", ci)}," +
                  $"\"sprintRamp\":{_sim.SprintRampValue.ToString("F2", ci)}," +
                  $"\"mana\":{_sim.NecroState.Mana.ToString("F1", ci)}}}"));
               break;
            }

            // Set the tether anchor to the unit/corpse nearest (x,y) — same as Shift+T.
            // Headless has no cursor, so the coords stand in for the mouse world position.
            case "tether": {
               if (c.Args.Length < 2) {
                  c.Complete(Necroking.Dev.DevServer.Error("tether needs: <x> <y>"));
                  break;
               }
               var tp = new Vec2(DevFloat(c.Args[0]), DevFloat(c.Args[1]));
               _tetherAnchor = TryPickTetherEnd(tp, out var anchor) ? anchor : (TetherEnd?)null;
               c.Complete(Necroking.Dev.DevServer.Ok(_tetherAnchor.HasValue
                  ? $"tether anchor set ({_tetherAnchor.Value.Kind})"
                  : "no unit/corpse near point"));
               break;
            }

            // Attach/detach a rope — same as Shift+R. With coords, (x,y) stands in for the
            // cursor; with no args the necromancer quick-drags the nearest free corpse.
            case "rope": {
               Vec2 rp = c.Args.Length >= 2
                  ? new Vec2(DevFloat(c.Args[0]), DevFloat(c.Args[1]))
                  : (_sim.NecromancerIndex >= 0 ? _sim.Units[_sim.NecromancerIndex].Position : Vec2.Zero);
               string msg = HandleRopeKey(rp, _sim.NecromancerIndex);
               c.Complete(Necroking.Dev.DevServer.Ok(msg));
               break;
            }

            // Simulate a left-click on the world at (x,y) for the corpse-pile
            // gather flow (headless has no real mouse). Mirrors the Game1.Update
            // click handler: pick up now if in range, else walk over and grab.
            case "pile_click": {
               if (_sim.NecromancerIndex < 0) {
                  c.Complete(Necroking.Dev.DevServer.Error("no necromancer in the sim"));
                  break;
               }
               if (c.Args.Length < 2) {
                  c.Complete(Necroking.Dev.DevServer.Error("pile_click needs: <x> <y>"));
                  break;
               }
               int ni = _sim.NecromancerIndex;
               var mw = new Vec2(DevFloat(c.Args[0]), DevFloat(c.Args[1]));
               int pile = FindCorpsePileUnderCursor(mw);
               if (pile < 0) {
                  c.Complete(Necroking.Dev.DevServer.Error($"no corpse pile under ({mw.X},{mw.Y})"));
                  break;
               }
               if (TryTakeCorpseFromPile(ni, pile))
                  c.Complete(Necroking.Dev.DevServer.Ok($"picked up a corpse from pile obj{pile}"));
               else
                  c.Complete(Necroking.Dev.DevServer.Error($"too far / busy / empty — no pickup from pile obj{pile}"));
               break;
            }

            // Run the one-shot "loose corpses on a pile → stock" absorb pass (normally
            // fires on map load). Lets headless tests trigger it after placing corpses.
            case "absorb_piles": {
               _workerSystem.AbsorbCorpsesOnPiles();
               c.Complete(Necroking.Dev.DevServer.Ok("absorbed loose corpses sitting on piles"));
               break;
            }

            // Set HP (and optionally MaxHP) on matching units.
            case "set_hp": {
               if (c.Args.Length < 2) {
                  c.Complete(Necroking.Dev.DevServer.Error("set_hp needs: <selector> <hp> [maxHp]"));
                  break;
               }

               bool hasMax = c.Args.Length >= 3;
               int hp = (int)DevFloat(c.Args[c.Args.Length - (hasMax ? 2 : 1)]);
               int maxHp = hasMax ? (int)DevFloat(c.Args[c.Args.Length - 1]) : 0;
               var idxs = DevResolveUnits(string.Join(" ", c.Args, 0, c.Args.Length - (hasMax ? 2 : 1)));
               foreach (int i in idxs) {
                  if (hasMax) _sim.UnitsMut[i].Stats.MaxHP = maxHp;
                  _sim.UnitsMut[i].Stats.HP = hp;
               }

               c.Complete(Necroking.Dev.DevServer.Ok(
                  $"set HP={hp}{(hasMax ? $" MaxHP={maxHp}" : "")} on {idxs.Count} unit(s)"));
               break;
            }

            // Set mana on matching units, or on the necromancer's NecroState
            // (selector "necro"). Used to enable spell-cast tests.
            case "set_mana": {
               if (c.Args.Length < 2) {
                  c.Complete(Necroking.Dev.DevServer.Error("set_mana needs: <selector|necro> <mana> [maxMana]"));
                  break;
               }

               bool hasMax = c.Args.Length >= 3;
               float mana = DevFloat(c.Args[c.Args.Length - (hasMax ? 2 : 1)]);
               float maxMana = hasMax ? DevFloat(c.Args[c.Args.Length - 1]) : 0f;
               string sel = string.Join(" ", c.Args, 0, c.Args.Length - (hasMax ? 2 : 1));
               if (sel.Equals("necro", StringComparison.OrdinalIgnoreCase) ||
                   sel.Equals("necromancer", StringComparison.OrdinalIgnoreCase)) {
                  if (hasMax) _sim.NecroState.MaxMana = maxMana;
                  _sim.NecroState.Mana = mana;
                  c.Complete(Necroking.Dev.DevServer.Ok($"necro mana={mana}{(hasMax ? $" max={maxMana}" : "")}"));
                  break;
               }

               var idxs = DevResolveUnits(sel);
               foreach (int i in idxs) {
                  if (hasMax) _sim.UnitsMut[i].MaxMana = maxMana;
                  _sim.UnitsMut[i].Mana = mana;
               }

               c.Complete(Necroking.Dev.DevServer.Ok($"set mana={mana} on {idxs.Count} unit(s)"));
               break;
            }

            // Hand an AI unit a (different) spell to cast: sets Unit.SpellID (what
            // CasterUnitHandler casts through the shared pipeline) and gives it a
            // default mana pool if it has none. Path requirements still gate the
            // cast — set them on the def or a buff. window.dev('set_spell',['9','nether_darts'])
            case "set_spell": {
               if (c.Args.Length < 2) {
                  c.Complete(Necroking.Dev.DevServer.Error("set_spell needs: <selector> <spellID>"));
                  break;
               }
               string spellId = c.Args[c.Args.Length - 1];
               if (_gameData.Spells.Get(spellId) == null) {
                  c.Complete(Necroking.Dev.DevServer.Error($"unknown spell: {spellId}"));
                  break;
               }
               string spellSel = string.Join(" ", c.Args, 0, c.Args.Length - 1);
               var spellIdxs = DevResolveUnits(spellSel);
               foreach (int i in spellIdxs) {
                  _sim.UnitsMut[i].SpellID = spellId;
                  if (_sim.UnitsMut[i].MaxMana <= 0f) {
                     _sim.UnitsMut[i].MaxMana = 50f;
                     _sim.UnitsMut[i].Mana = 50f;
                     _sim.UnitsMut[i].ManaRegen = 1f;
                  }
               }
               c.Complete(Necroking.Dev.DevServer.Ok($"set spell={spellId} on {spellIdxs.Count} unit(s)"));
               break;
            }

            // Swap the necromancer's UnitDef in-place (same path the
            // Metamorphosis tree uses on Become Pale Acolyte / Wight / etc.).
            // For testing different player chassis without restarting the map.
            // window.dev('set_necro_type',['wight'])
            case "set_necro_type": {
               if (c.Args.Length < 1) {
                  c.Complete(Necroking.Dev.DevServer.Error("set_necro_type needs: <unitDefId>"));
                  break;
               }
               string newDefId = c.Args[0];
               if (_gameData.Units.Get(newDefId) == null) {
                  c.Complete(Necroking.Dev.DevServer.Error($"unknown unit def: {newDefId}"));
                  break;
               }
               int necroIdx = FindNecromancer();
               if (necroIdx < 0) {
                  c.Complete(Necroking.Dev.DevServer.Error("no necromancer in the world"));
                  break;
               }
               _sim.TransformUnit(necroIdx, newDefId);
               RebuildUnitAnim(necroIdx, newDefId);
               c.Complete(Necroking.Dev.DevServer.Ok($"necromancer -> {newDefId}"));
               break;
            }

            // Multiplayer session control, mirroring the pause-menu Multiplayer UI.
            // net host [port] | net connect <ip> [port] | net stop | net status
            case "net": {
               string sub = c.Args.Length >= 1 ? c.Args[0].ToLowerInvariant() : "status";
               switch (sub) {
                  case "host": {
                     int port = c.Args.Length >= 2 ? (int)DevFloat(c.Args[1]) : Necroking.Net.NetProtocol.DefaultPort;
                     bool ok = _net.StartHost("", port);
                     c.Complete(ok ? Necroking.Dev.DevServer.Ok($"hosting on {port}")
                                   : Necroking.Dev.DevServer.Error("failed to host (see net status log)"));
                     break;
                  }
                  case "connect": {
                     if (c.Args.Length < 2) { c.Complete(Necroking.Dev.DevServer.Error("net connect <ip> [port]")); break; }
                     int port = c.Args.Length >= 3 ? (int)DevFloat(c.Args[2]) : Necroking.Net.NetProtocol.DefaultPort;
                     bool ok = _net.Connect(c.Args[1], port);
                     c.Complete(ok ? Necroking.Dev.DevServer.Ok($"connecting to {c.Args[1]}:{port}")
                                   : Necroking.Dev.DevServer.Error("connect failed (see net status log)"));
                     break;
                  }
                  case "stop":
                     _net.Stop();
                     c.Complete(Necroking.Dev.DevServer.Ok("session stopped"));
                     break;
                  default: {
                     var js = System.Text.Json.JsonSerializer.Serialize(new {
                        mode = _net.Mode.ToString(),
                        status = _net.StatusLine,
                        localPlayerId = _net.LocalPlayerId,
                        remotePlayers = _net.RemotePlayers.Count,
                        ghosts = _netGhosts.Count,
                        log = _net.Log,
                     });
                     c.Complete(Necroking.Dev.DevServer.OkRaw(js));
                     break;
                  }
               }
               break;
            }

            // Override the cursor position for headless hover testing (tooltips,
            // hover highlights). window.dev('mousepos',['541','685']) ; 'clear' removes it.
            case "mousepos": {
               if (c.Args.Length >= 1 && c.Args[0].Equals("clear", StringComparison.OrdinalIgnoreCase)) {
                  _devMouseOverride = null;
                  c.Complete(Necroking.Dev.DevServer.Ok("mouse override cleared"));
                  break;
               }
               if (c.Args.Length < 2) {
                  c.Complete(Necroking.Dev.DevServer.Error("mousepos needs: <x> <y>  (or 'clear')"));
                  break;
               }
               _devMouseOverride = new Microsoft.Xna.Framework.Vector2(DevFloat(c.Args[0]), DevFloat(c.Args[1]));
               c.Complete(Necroking.Dev.DevServer.Ok($"mouse override = ({c.Args[0]},{c.Args[1]})"));
               break;
            }

            // Inject a full synthetic OS mouse state — position, held buttons and
            // CUMULATIVE wheel value (MouseState-style). Unlike mousepos/ui_click
            // this reaches the raw main-menu-family paths (main menu, scenario
            // list, load menu) which read Mouse.GetState() directly, so headless
            // runs can drive those menus: press/hold across frames for scrollbar
            // drags, wheel deltas for scrolling. Persists until 'raw_mouse clear'.
            // window.dev('raw_mouse',['640','300','left','-120']) ; buttons: left|right|none
            case "raw_mouse": {
               if (c.Args.Length >= 1 && c.Args[0].Equals("clear", StringComparison.OrdinalIgnoreCase)) {
                  _devRawMouse = null;
                  c.Complete(Necroking.Dev.DevServer.Ok("raw_mouse cleared"));
                  break;
               }
               if (c.Args.Length < 2) {
                  c.Complete(Necroking.Dev.DevServer.Error("raw_mouse needs: <x> <y> [left|right|none] [wheelValue]  (or 'clear')"));
                  break;
               }
               int rmX = (int)DevFloat(c.Args[0]), rmY = (int)DevFloat(c.Args[1]);
               string btn = c.Args.Length >= 3 ? c.Args[2].ToLowerInvariant() : "none";
               int wheelVal = c.Args.Length >= 4 ? (int)DevFloat(c.Args[3])
                  : (_devRawMouse?.ScrollWheelValue ?? 0);
               _devRawMouse = new Microsoft.Xna.Framework.Input.MouseState(rmX, rmY, wheelVal,
                  btn == "left" ? Microsoft.Xna.Framework.Input.ButtonState.Pressed : Microsoft.Xna.Framework.Input.ButtonState.Released,
                  Microsoft.Xna.Framework.Input.ButtonState.Released,
                  btn == "right" ? Microsoft.Xna.Framework.Input.ButtonState.Pressed : Microsoft.Xna.Framework.Input.ButtonState.Released,
                  Microsoft.Xna.Framework.Input.ButtonState.Released,
                  Microsoft.Xna.Framework.Input.ButtonState.Released);
               c.Complete(Necroking.Dev.DevServer.Ok($"raw_mouse ({rmX},{rmY}) btn={btn} wheel={wheelVal}"));
               break;
            }

            // Set the F7 gameplay-debug overlay: 0=Off, 1=Horde, 2=Unit Info.
            // window.dev('gpdebug',['1'])  (no arg → Horde)
            case "gpdebug": {
               // Euclidean wrap — C# % keeps the sign, and a negative mode is "off"-ish garbage.
               int gpMode = c.Args.Length >= 1 ? (int)DevFloat(c.Args[0]) : 1;
               _gameplayDebugMode = ((gpMode % 3) + 3) % 3;
               c.Complete(Necroking.Dev.DevServer.Ok($"gameplayDebugMode={_gameplayDebugMode}"));
               break;
            }

            // The dev-server stand-in for pressing Q mid spirit-walk.
            case "spirit_root":
               if (!GameSystems.SpiritWalkSystem.Active) {
                  c.Complete(Necroking.Dev.DevServer.Error("not spirit walking"));
                  break;
               }
               GameSystems.SpiritWalkSystem.RootSpirit(this);
               c.Complete(Necroking.Dev.DevServer.Ok("spirit rooted as scrying eye"));
               break;

            // Necromancer casts a spell at a world point (full player pipeline).
            // Cast may fail on mana/cooldown/range — set_mana necro first if needed.
            case "cast": {
               if (c.Args.Length < 3) {
                  c.Complete(Necroking.Dev.DevServer.Error("cast needs: <spellID> <x> <y>"));
                  break;
               }

               int necroIdx = _sim.NecromancerIndex;
               if (necroIdx < 0) {
                  c.Complete(Necroking.Dev.DevServer.Error("no necromancer in world"));
                  break;
               }

               if (_gameData.Spells.Get(c.Args[0]) == null) {
                  c.Complete(Necroking.Dev.DevServer.Error($"unknown spell: {c.Args[0]}"));
                  break;
               }

               var target = new Vec2(DevFloat(c.Args[1]), DevFloat(c.Args[2]));
               var res = DispatchSpellCast(c.Args[0], necroIdx, 0, target);
               // hold=<secs>: keep a Beam/Drain channel alive as if the slot key
               // stayed held (dev casts have no physical key to hold).
               string? holdOpt = c.Opt("hold");
               if (res == Necroking.GameSystems.CastResult.Success && holdOpt != null)
                  _devChannelHoldTimer = DevFloat(holdOpt);
               string detail = $"cast {c.Args[0]} -> {res}";
               // Path failure must NOT read as a mana failure — name the path(s) the
               // necromancer still needs so the reason is unambiguous.
               if (res == Necroking.GameSystems.CastResult.MissingPath) {
                  string need = Necroking.GameSystems.SpellCaster.DescribeMissingPath(
                     _gameData.Spells.Get(c.Args[0]), _gameData, _sim.Units, necroIdx);
                  detail = $"cast {c.Args[0]} -> MissingPath (needs {need})";
               }
               c.Complete(Necroking.Dev.DevServer.Ok(detail));
               break;
            }

            // Spawn a lightning visual directly in the sim (no cast pipeline, no
            // windup, no damage) so screenshots can be timed deterministically.
            // devctl: cmd spawn_lightning <zap|strike|beam|drain> <spellID> <x> <y>
            //         [duration=5] [width_scale=1]
            // beam/drain anchor caster=necromancer, target=unit closest to (x,y).
            case "spawn_lightning": {
               if (c.Args.Length < 4) {
                  c.Complete(Necroking.Dev.DevServer.Error(
                     "spawn_lightning needs: <zap|strike|beam|drain> <spellID> <x> <y> [duration=5] [width_scale=1]"));
                  break;
               }
               string kind = c.Args[0].ToLowerInvariant();
               var lSpell = _gameData.Spells.Get(c.Args[1]);
               if (lSpell == null) {
                  c.Complete(Necroking.Dev.DevServer.Error($"unknown spell: {c.Args[1]}"));
                  break;
               }
               int lNecro = _sim.NecromancerIndex;
               if (lNecro < 0) {
                  c.Complete(Necroking.Dev.DevServer.Error("no necromancer in world"));
                  break;
               }
               var lTarget = new Vec2(DevFloat(c.Args[2]), DevFloat(c.Args[3]));
               float lDur = c.Opt("duration") != null ? DevFloat(c.Opt("duration")!) : 5f;
               float lScale = c.Opt("width_scale") != null ? DevFloat(c.Opt("width_scale")!) : 1f;
               // jitter_hz=<n> overrides the style's JitterHz (0 = frozen shape).
               string? lJitterOpt = c.Opt("jitter_hz");

               // Closest unit to the target point (any faction, not the necro) for
               // the unit-anchored kinds.
               int lUnitIdx = -1; float lBest = float.MaxValue;
               for (int i = 0; i < _sim.Units.Count; i++) {
                  if (i == lNecro || !_sim.Units[i].Alive) continue;
                  float d = (_sim.Units[i].Position - lTarget).LengthSq();
                  if (d < lBest) { lBest = d; lUnitIdx = i; }
               }

               string? lErr = null;
               switch (kind) {
                  case "zap": {
                     var st = lSpell.BuildStrikeStyle();
                     st.CoreWidth *= lScale; st.GlowWidth *= lScale;
                     if (lJitterOpt != null) st.JitterHz = DevFloat(lJitterOpt);
                     _sim.Lightning.SpawnZap(_sim.Units[lNecro].EffectSpawnPos2D, lTarget,
                        lDur, st, _sim.Units[lNecro].EffectSpawnHeight, 1f);
                     break;
                  }
                  case "strike": {
                     var st = lSpell.BuildStrikeStyle();
                     st.CoreWidth *= lScale; st.GlowWidth *= lScale;
                     if (lJitterOpt != null) st.JitterHz = DevFloat(lJitterOpt);
                     _sim.Lightning.SpawnStrike(lTarget, 0f, lDur, 0f, 0, st, lSpell.Id,
                        telegraphVisible: false);
                     break;
                  }
                  case "beam": {
                     if (lUnitIdx < 0) { lErr = "beam needs a target unit"; break; }
                     var st = lSpell.BuildBeamStyle();
                     st.CoreWidth *= lScale; st.GlowWidth *= lScale;
                     if (lJitterOpt != null) st.JitterHz = DevFloat(lJitterOpt);
                     // damagePerTick -1: visual-only sentinel — 0 now means "tick
                     // and let the opposed DRN roll decide" (LightningSystem).
                     _sim.Lightning.SpawnBeam(_sim.Units[lNecro].Id, _sim.Units[lUnitIdx].Id,
                        lSpell.Id, -1, 999f, 0f, st, lDur);
                     break;
                  }
                  case "drain": {
                     if (lUnitIdx < 0) { lErr = "drain needs a target unit"; break; }
                     var vis = lSpell.BuildDrainVisuals();
                     vis.CoreWidth *= lScale; vis.GlowWidth *= lScale;
                     // damagePerTick -1: visual-only sentinel (see beam above).
                     _sim.Lightning.SpawnDrain(_sim.Units[lNecro].Id, _sim.Units[lUnitIdx].Id,
                        lSpell.Id, -1, 999f, 0f, 0, false, lDur, vis);
                     break;
                  }
                  default:
                     lErr = $"unknown kind: {kind}";
                     break;
               }
               if (lErr != null)
                  c.Complete(Necroking.Dev.DevServer.Error(lErr));
               else
                  c.Complete(Necroking.Dev.DevServer.Ok(
                     $"spawned {kind} ({lSpell.Id}) dur={lDur} scale={lScale}" +
                     (lUnitIdx >= 0 && (kind == "beam" || kind == "drain") ? $" targetIdx={lUnitIdx}" : "")));
               break;
            }

            // Simulate a world mouse click at a world point (the Game1.WorldClicks
            // dispatch: LMB = open building panel / pile gather / attack clicked
            // enemy; RMB = forage / pile gather / attack). Runs the same handlers
            // as a real click, minus the HUD/popup mouse routing.
            case "click": {
               if (c.Args.Length < 2) {
                  c.Complete(Necroking.Dev.DevServer.Error("click needs: <x> <y> [right]"));
                  break;
               }
               var clickWorld = new Vec2(DevFloat(c.Args[0]), DevFloat(c.Args[1]));
               bool right = c.Args.Length >= 3
                  && c.Args[2].Equals("right", StringComparison.OrdinalIgnoreCase);
               int clickNecro = _sim.NecromancerIndex;
               int sw = GraphicsDevice.Viewport.Width, sh = GraphicsDevice.Viewport.Height;
               bool consumedBefore = _input.IsMouseConsumed;
               if (right)
                  HandleWorldRightClick(clickWorld, clickNecro);
               else {
                  var screenPos = _camera.WorldToScreen(clickWorld, 0f, sw, sh);
                  HandleWorldLeftClick(sw, sh, (int)screenPos.X, (int)screenPos.Y,
                     clickWorld, clickNecro);
               }
               bool acted = _input.IsMouseConsumed && !consumedBefore;
               c.Complete(Necroking.Dev.DevServer.Ok(
                  $"click {(right ? "RMB" : "LMB")} at ({clickWorld.X:F1},{clickWorld.Y:F1}) -> {(acted ? "handled" : "no world action")}"));
               break;
            }

            // Inject a SCREEN-space click through the real UI routing pipeline
            // (UIRouter.DispatchInput, which includes the editor sub-popup stack
            // via ModalStackLayer) with a synthetic InputState — verifies z-order,
            // consumption, and hover ownership exactly as a real mouse press
            // would, headless-safe. Reports the registry region under the point,
            // the hover-owning layer, and whether the click was consumed.
            case "ui_click": {
               if (c.Args.Length < 2) {
                  c.Complete(Necroking.Dev.DevServer.Error("ui_click needs: <sx> <sy> [right]"));
                  break;
               }
               int sx = (int)DevFloat(c.Args[0]), sy = (int)DevFloat(c.Args[1]);
               bool uiRight = c.Args.Length >= 3
                  && c.Args[2].Equals("right", StringComparison.OrdinalIgnoreCase);
               int usw = GraphicsDevice.Viewport.Width, ush = GraphicsDevice.Viewport.Height;
               var btnUp = Microsoft.Xna.Framework.Input.ButtonState.Released;
               var btnDown = Microsoft.Xna.Framework.Input.ButtonState.Pressed;
               var upPrev = new Microsoft.Xna.Framework.Input.MouseState(sx, sy, 0,
                  btnUp, btnUp, btnUp, btnUp, btnUp);
               var downNow = new Microsoft.Xna.Framework.Input.MouseState(sx, sy, 0,
                  uiRight ? btnUp : btnDown, btnUp, uiRight ? btnDown : btnUp,
                  btnUp, btnUp);
               var kbNow = Microsoft.Xna.Framework.Input.Keyboard.GetState();
               var syn = new Necroking.Core.InputState();
               syn.Capture(downNow, upPrev, kbNow, kbNow);
               syn.MouseOverUI = _uiHits.Hit(sx, sy);
               string? uiHitId = _uiHits.HitId(sx, sy);
               _uiRouter.DispatchInput(syn,
                  new Necroking.UI.UICtx(usw, ush, _clock.VisualTime));
               string hoverId = _uiRouter.HoverLayer?.Id ?? "none";
               c.Complete(Necroking.Dev.DevServer.OkRaw(
                  $"{{\"hit\":\"{uiHitId ?? "none"}\"," +
                  $"\"hover\":\"{hoverId}\"," +
                  $"\"consumed\":{(syn.IsMouseConsumed ? "true" : "false")}}}"));
               break;
            }

            // Inject a key press through the router dispatch (currently ESC
            // only) — verifies the Closable-walk ESC routing headlessly: the
            // topmost closable layer cancels, the key is consumed, nothing
            // below double-closes.
            case "ui_key": {
               string keyName = c.Args.Length >= 1 ? c.Args[0].ToLowerInvariant() : "escape";
               if (keyName != "escape" && keyName != "esc") {
                  c.Complete(Necroking.Dev.DevServer.Error("ui_key supports: escape"));
                  break;
               }
               int ksw = GraphicsDevice.Viewport.Width, ksh = GraphicsDevice.Viewport.Height;
               var kbtnUp = Microsoft.Xna.Framework.Input.ButtonState.Released;
               var kMouse = new Microsoft.Xna.Framework.Input.MouseState(0, 0, 0,
                  kbtnUp, kbtnUp, kbtnUp, kbtnUp, kbtnUp);
               var synk = new Necroking.Core.InputState();
               synk.Capture(kMouse, kMouse,
                  new Microsoft.Xna.Framework.Input.KeyboardState(Microsoft.Xna.Framework.Input.Keys.Escape),
                  new Microsoft.Xna.Framework.Input.KeyboardState());
               _uiRouter.DispatchInput(synk,
                  new Necroking.UI.UICtx(ksw, ksh, _clock.VisualTime));
               bool escConsumed = synk.IsKeyConsumed(Microsoft.Xna.Framework.Input.Keys.Escape);
               c.Complete(Necroking.Dev.DevServer.OkRaw(
                  $"{{\"consumed\":{(escConsumed ? "true" : "false")}}}"));
               break;
            }

            // Spawn a fireball projectile directly (deterministic, no mana/anim
            // gating). From the necromancer if present, else from offset of target.
            case "fireball": {
               if (c.Args.Length < 2) {
                  c.Complete(Necroking.Dev.DevServer.Error("fireball needs: <x> <y> [damage] [radius] [name]"));
                  break;
               }

               var target = new Vec2(DevFloat(c.Args[0]), DevFloat(c.Args[1]));
               int dmg = c.Args.Length >= 3 ? (int)DevFloat(c.Args[2]) : 25;
               float radius = c.Args.Length >= 4 ? DevFloat(c.Args[3]) : 1.5f;
               string name = c.Args.Length >= 5 ? c.Args[4] : "Fireball";
               int ni = _sim.NecromancerIndex;
               Vec2 from = ni >= 0 ? _sim.Units[ni].Position : target + new Vec2(-6f, 0f);
               uint owner = ni >= 0 ? _sim.Units[ni].Id : 0u;
               _sim.Projectiles.Spawn(from, target, Faction.Undead, owner,
                  ProjectileType.Explosive, dmg, GameSystems.ProjectileManager.MagicSpeed, lob: true,
                  aoeRadius: radius, weaponName: name);
               c.Complete(Necroking.Dev.DevServer.Ok(
                  $"fireball from ({from.X:F1},{from.Y:F1}) -> ({target.X:F1},{target.Y:F1}) dmg={dmg} r={radius}"));
               break;
            }

            // Queue a batch script: a list of commands with waits between them,
            // stepped over the game's update loop. Returns a job id immediately;
            // poll progress + results with `job`. opts.script = [ {cmd,args,opts}
            // | {wait:<simSecs>} | {wait_real:<secs>} | {wait_frames:<n>}
            // | {shot:"name", ...screenshotOpts} ].
            case "batch": {
               // One job at a time — silently replacing a running job abandons it mid-script.
               if (_devJob != null && !_devJob.Done) {
                  c.Complete(Necroking.Dev.DevServer.Error(
                     $"job '{_devJob.Id}' still running (step {_devJob.Cursor}/{_devJob.Steps.Count}) — poll 'job' until done or 'job cancel' first"));
                  break;
               }
               string? raw = c.Opt("script");
               if (string.IsNullOrEmpty(raw)) {
                  c.Complete(Necroking.Dev.DevServer.Error(
                     "batch needs opts.script = [ {cmd,args,opts} | {wait:n} | {wait_real:n} | {wait_frames:n} | {shot:\"name\"} ]"));
                  break;
               }

               var steps = ParseDevScript(raw, out string perr);
               if (steps == null) {
                  c.Complete(Necroking.Dev.DevServer.Error($"bad script: {perr}"));
                  break;
               }

               string jobId = $"job{++_devJobSeq}";
               _devJob = new Necroking.Dev.DevJob { Id = jobId, Steps = steps };
               c.Complete(Necroking.Dev.DevServer.OkRaw($"{{\"jobId\":\"{jobId}\",\"steps\":{steps.Count}}}"));
               break;
            }

            // Poll the active batch job: `job [jobId]`. `job cancel` aborts it.
            case "job": {
               if (_devJob == null) {
                  c.Complete(Necroking.Dev.DevServer.Error("no batch job (run 'batch' first)"));
                  break;
               }

               if (c.Args.Length > 0 && c.Args[0].Equals("cancel", StringComparison.OrdinalIgnoreCase)) {
                  _devJob.Done = true;
                  _devJob.Canceled = true;
               } else if (c.Args.Length > 0 && !c.Args[0].Equals(_devJob.Id, StringComparison.OrdinalIgnoreCase)) {
                  // Stale id from an earlier batch — say so instead of returning the wrong job's status.
                  c.Complete(Necroking.Dev.DevServer.Error($"no job '{c.Args[0]}' (current is '{_devJob.Id}')"));
                  break;
               }

               c.Complete(Necroking.Dev.DevServer.OkRaw(DevJobStatusJson(_devJob)));
               break;
            }

            // Inject a registry entry (spell/unit/item/buff/…) into the LIVE game
            // from JSON, like a row in data/<file>.json — runtime only, never saved.
            // If the matching editor is open, the new entry is selected. (DevAddData
            // lives in Game1.DevData.cs.)
            case "add_data":
            case "add_json":
               DevAddData(c);
               break;

            // Write every data registry back to data/*.json — exactly what the
            // editors' Save button does (GameData.Save). Lets headless sessions
            // exercise/migrate the on-disk format (e.g. omit-defaults pruning).
            case "save_data": {
               if (_gameData == null) { c.Complete(Necroking.Dev.DevServer.Error("no game data loaded")); break; }
               bool saved = _gameData.Save();
               c.Complete(saved
                  ? Necroking.Dev.DevServer.Ok("data saved")
                  : Necroking.Dev.DevServer.Error("save failed — see log"));
               break;
            }

            // Discovery: list every dev command with a one-line signature.
            case "help":
            case "commands": {
               // MAINTENANCE: this array must list EVERY case in the switch above —
               // the drive-game skill promises `help` is complete discovery, so a
               // new command that isn't added here is invisible. Keep the groups.
               var cmds = new[] {
                  // liveness / state dumps
                  "ping", "state", "help", "mem", "perf [reset]", "census",
                  "units [selector]", "unit <selector>", "corpses", "combat_log [n]",
                  "jobs", "cooldowns", "locomotion [selector]  (alias loco)",
                  "fog <x> <y>", "setting <dotted.path> [value]  (alias set_setting)",
                  // spawning & world objects
                  "spawn <type> <x> <y>", "spawn_def <unitID> <x> <y> [count]",
                  "spawn_horde <unitID> <x> <y> [count]",
                  "spawn_wild <unitID> <x> <y>",
                  "place_obj <defId> <x> <y> [scale]",
                  "procgen_paint <styleName> <x> <y> [seconds]",
                  // unit manipulation
                  "damage <selector> <amount>", "kill <selector>", "remove <selector>",
                  "zombify [selector]", "set_ai <selector> <AIBehavior>", "move <selector> <x> <y>",
                  "set_hp <selector> <hp> [maxHp]", "set_mana <selector|necro> <mana> [maxMana]",
                  "set_spell <selector> <spellID>", "set_necro_type <unitDefId>", "godmode [on|off]",
                  "walk_necro <x> <y>  (or 'clear'; cancelled by any WASD press)",
                  "mark <selector|clear>", "unmark [selector]",
                  // spells & reanimation
                  "cast <spellID> <x> <y>", "spirit_root  (Q stand-in: root the walking spirit)",
                  "fireball <x> <y> [dmg] [radius] [name]",
                  "reanim_at <x> <y> [defId] [riseSpeed] [fogSpeed]",
                  "reanim_into <zombieDefId> [x] [y]", "learn_skill <skillId>",
                  "set_spell_slot <slot 0-9> <spellID|->", "give_item <itemID> [qty]",
                  "save_game <name>", "load_game <name>",
                  "save_map [name]", "reload_map  (map editor's Reload Map button)",
                  // headless input (clicks / mouse / hover)
                  "click <x> <y> [right]", "mousepos <x> <y>|clear", "pile_click <x> <y>",
                  "hover <selector>|clear", "hover_obj <index>|clear",
                  "hover_at <screenX> <screenY>", "hover_variant <-1..20>",
                  "tether <x> <y>", "rope [x y]",
                  // worker economy
                  "assign_worker <unitId> [graveObjIdx]", "unassign_worker <unitId>",
                  "stock_add <buildingDefId> <resource> <amount>", "auto_assign", "absorb_piles",
                  "worker_scene", "ui_job_board", "ui_grave_roster [graveObjIdx]",
                  // camera / time / game flow
                  "camera <x> <y> [zoom]", "free_camera [on|off]", "speed <n>", "pause", "resume",
                  "start_game [map]  (default empty_test)", "start_scenario <name>", "menu <new_game|scenarios|main_menu|quit>",
                  "window <show|hide|toggle>",
                  // rendering & fx
                  "screenshot [name]  opts:{no_ui,no_ground,downsample_to}",
                  "pass list|on <name>|off <name>",
                  "groundfog add <x> <y> [radius] [density] [ttl] | at_camera [radius] [density] | clear",
                  "depthfog [on|off]", "scatterglow [on|off] | strength <v>", "gpdebug [0|1|2]",
                  // UI panels
                  "panels", "panel <name> [tab]", "tab <name>", "ui_rects",
                  "overlay <name> [open|close|toggle]", "select <name|id|index>",
                  // batching & data injection
                  "batch  opts:{script:[{cmd,args,opts}|{wait:n}|{wait_real:n}|{wait_frames:n}|{shot:\"name\",...shotOpts}]}",
                  "job [jobId|cancel]",
                  "add_data [spell|unit|item|buff|weapon|armor|shield|potion|flipbook]  opts:{json:<entry|array|datafile>,open}  (alias add_json)",
                  "save_data",
               };
               c.Complete(Necroking.Dev.DevServer.OkRaw(
                  $"{{\"selectors\":\"all|necro|undead|human|animal|<index>|id:<n>|<unitDefId>|<UnitType>\",\"commands\":{System.Text.Json.JsonSerializer.Serialize(cmds)}}}"));
               break;
            }

            default:
               c.Complete(Necroking.Dev.DevServer.Error($"unknown cmd: {c.Cmd}"));
               break;
         }
      }
      catch (Exception ex) {
         c.Complete(Necroking.Dev.DevServer.Error(ex.Message));
      }
   }

   /// <summary>Switch <see cref="_menuState"/> to a named UI panel for the dev
   /// control channel. Panels other than the main menu / scenario list assume an
   /// in-game world, so a default game is started first when none is loaded.
   /// Returns false for an unknown name.</summary>
   bool SetUiPanel(string name) {
      switch (name.ToLowerInvariant()) {
         case "main_menu":
         case "mainmenu":
            _menuState = MenuState.MainMenu;
            _clock.ClearAllPauses();
            _gameWorldLoaded = false;
            return true;
         case "scenarios":
         case "scenario_list":
            _menuState = MenuState.ScenarioList;
            _scenarioScrollPx = 0f;
            return true;
         case "load_menu":
         case "loadmenu":
            _loadGameWindow.Open();
            return true;
      }

      // Everything below renders over a live world — load one if needed.
      if (!_gameWorldLoaded) StartGame();

      switch (name.ToLowerInvariant()) {
         case "game":
         case "gameplay":
         case "none":
            _menuState = MenuState.None;
            _clock.ClearAllPauses();
            return true;
         case "pause":
         case "pause_menu":
         case "esc":
            _menuState = MenuState.PauseMenu;
            _clock.Pause(GameClock.PauseSource.User); // emulates the ESC path
            return true;
         case "settings":
         case "options":
            _menuState = MenuState.Settings;
            return true;
         case "save_menu":
         case "savemenu":
            OpenSaveMenu();
            _clock.Pause(GameClock.PauseSource.User); // reached from the pause menu in normal play
            return true;
         case "multiplayer":
         case "mp":
            _menuState = MenuState.Multiplayer;
            return true;
         case "unit_editor":
         case "uniteditor":
            _menuState = MenuState.UnitEditor;
            _clock.ClearAllPauses();
            return true;
         case "spell_editor":
         case "spelleditor":
            _menuState = MenuState.SpellEditor;
            _clock.ClearAllPauses();
            return true;
         case "map_editor":
         case "mapeditor":
            _menuState = MenuState.MapEditor;
            _clock.ClearAllPauses();
            _mapEditor.SuppressClicksUntilRelease();
            return true;
         case "ui_editor":
         case "uieditor":
            EnsureUIEditorInitialized();
            _menuState = MenuState.UIEditor;
            _clock.ClearAllPauses();
            return true;
         case "item_editor":
         case "itemeditor":
            _menuState = MenuState.ItemEditor;
            _clock.ClearAllPauses();
            return true;
         default:
            return false;
      }
   }

   /// <summary>Set the active tab on the currently open panel (map editor, UI
   /// editor, or settings). Returns false if the name doesn't match a tab of the
   /// open panel, or no tabbed panel is open. Mirrors the scenario tab-switch
   /// hooks so there's one definition of what each tab name means.</summary>
   bool ApplyPanelTab(string tab) {
      switch (_menuState) {
         case MenuState.MapEditor:
            if (Enum.TryParse<Necroking.Editor.MapEditorTab>(tab, true, out var mt)) {
               _mapEditor.ActiveTab = mt;
               return true;
            }

            return false;
         case MenuState.UIEditor:
            if (Enum.TryParse<Necroking.Editor.UIEditorTab>(tab, true, out var ut)) {
               _uiEditor.ActiveTab = ut;
               return true;
            }

            return false;
         case MenuState.Settings:
            return _settingsWindow.SetActiveTab(tab);
         default:
            return false;
      }
   }

   /// <summary>Open/close/toggle an in-game overlay for the dev channel. All
   /// overlays render over a live world, so a default game is started if none is
   /// loaded. Returns a status string, or null for an unknown overlay name.</summary>
   string? SetOverlay(string name, string action, string[]? extra = null) {
      extra ??= System.Array.Empty<string>();
      if (!_gameWorldLoaded) StartGame();
      EnsureInventoryUIsInitialized();
      int sw = GraphicsDevice.Viewport.Width, sh = GraphicsDevice.Viewport.Height;

      switch (name.ToLowerInvariant()) {
         case "inventory":
            if (action == "close") _inventoryUI.Close();
            else if (action == "open") _inventoryUI.Open(sw, sh);
            else _inventoryUI.Toggle(sw, sh);
            return $"inventory visible={_inventoryUI.IsVisible}";

         case "character_stats":
         case "stats":
            // Only a Toggle() exists — derive open/close from current state.
            if (action == "toggle"
                || (action == "open") != _characterStatsUI.IsVisible)
               _characterStatsUI.Toggle();
            return $"character_stats visible={_characterStatsUI.IsVisible}";

         case "skill_book":
         case "skillbook":
            if (action == "close") _skillBookOverlay.Close();
            else if (action == "open") _skillBookOverlay.Open();
            else _skillBookOverlay.Toggle();
            return $"skill_book visible={_skillBookOverlay.IsVisible}";

         case "grimoire":
            // No public Open(); Toggle() opens, Hide() closes. "scroll <rows>" drives
            // the viewport for verifying spell-list scrolling without a wheel event.
            if (action == "close") _grimoireOverlay.Hide();
            else if (action == "open") {
               if (!_grimoireOverlay.IsVisible) _grimoireOverlay.Toggle();
            } else if (action == "scroll") {
               int rows = (extra.Length > 0 && int.TryParse(extra[0], out int rr)) ? rr : 1;
               return _grimoireOverlay.DebugScroll(rows);
            } else _grimoireOverlay.Toggle();

            return $"grimoire visible={_grimoireOverlay.IsVisible}";

         case "build_menu":
         case "buildmenu":
            if (action == "close") _buildingMenuUI.Close();
            else if (action == "toggle" || !_buildingMenuUI.IsVisible) {
               // Mirror the 'B' key path: docked-left panels are exclusive.
               if (!_buildingMenuUI.IsVisible) CloseSameSidePanels(PanelSide.Left, _buildingMenuUI);
               _buildingMenuUI.Toggle(sw, sh);
            }
            return $"build_menu visible={_buildingMenuUI.IsVisible} placement={_buildingMenuUI.IsPlacementActive}";

         case "crafting_menu":
         case "craftingmenu":
            if (action == "close") _craftingMenu.Close();
            else if (action == "toggle" || !_craftingMenu.IsVisible)
               ToggleCraftingMenu(sw, sh);
            return $"crafting_menu visible={_craftingMenu.IsVisible}";

         case "character_sheet":
         case "unit_info":
            if (action == "close") _unitInfoPanel.Hide();
            else if (_unitInfoPanel.IsVisible && action == "toggle") _unitInfoPanel.Hide();
            else if (_sim.NecromancerIndex >= 0) _unitInfoPanel.ShowForUnit(_sim.Units[_sim.NecromancerIndex].Id);
            else return "character_sheet: no necromancer to show";
            return $"character_sheet visible={_unitInfoPanel.IsVisible}";

         default:
            return null;
      }
   }

   /// <summary>Select an entry in whichever editor panel is open so its preview
   /// or detail view renders (for dev/preview screenshots). Accepts a numeric
   /// index, a def id, or a display name. Returns the selected entry's label, or
   /// null if no editor is open or nothing matched.</summary>
   string? SelectEditorEntry(string token) {
      switch (_menuState) {
         case MenuState.UnitEditor: return _unitEditor.DevSelect(token);
         case MenuState.SpellEditor: return _spellEditor.DevSelect(token);
         case MenuState.ItemEditor: return _itemEditor.DevSelect(token);
         case MenuState.UIEditor:
            if (int.TryParse(token, out int idx)) {
               _uiEditor.SelectedIndex = idx;
               return $"index {idx}";
            }

            return _uiEditor.SelectWidgetById(token) ? token : null;
         case MenuState.MapEditor:
            // Zones tab: select a zone by index/id/name so headless drives can
            // exercise the selected-state UI (handles, left village panel).
            // Falls back to Objects-tab env defs (placement ghost, def props).
            return _mapEditor.DevSelectZone(token) ?? _mapEditor.DevSelectObjectDef(token);
         default:
            return null;
      }
   }

   static float DevFloat(string s) =>
      float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

   /// <summary>Parse an on/off/toggle dev argument: args[0] of on/true/1 → true,
   /// off/false/0 → false, anything else (including no args) toggles
   /// <paramref name="current"/>. Shared by godmode / free_camera / depthfog.</summary>
   static bool DevToggle(string[] args, bool current) {
      string mode = args.Length >= 1 ? args[0].ToLowerInvariant() : "toggle";
      return mode switch {
         "on" or "true" or "1" => true,
         "off" or "false" or "0" => false,
         _ => !current,
      };
   }

   /// <summary>Walk a dotted settings path (matching either the JSON name or the C#
   /// property name, case-insensitive) down from <c>_gameData.Settings</c>, returning
   /// the owner object + final PropertyInfo of the leaf. Used by the `setting` dev
   /// command so live edits don't need a restart.</summary>
   bool ResolveSettingPath(string path, out object? owner, out PropertyInfo? leaf, out string err) {
      owner = null; leaf = null; err = "";
      object cur = _gameData.Settings;
      var segs = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
      if (segs.Length == 0) { err = "empty path"; return false; }
      for (int i = 0; i < segs.Length; i++) {
         var prop = FindSettingProp(cur.GetType(), segs[i]);
         if (prop == null) { err = $"no setting '{segs[i]}' on {cur.GetType().Name}"; return false; }
         if (i == segs.Length - 1) { owner = cur; leaf = prop; return true; }
         var next = prop.GetValue(cur);
         if (next == null) { err = $"'{segs[i]}' is null"; return false; }
         cur = next;
      }
      err = "unreachable"; return false;
   }

   /// <summary>Match a settings property by its [JsonPropertyName] or its C# name,
   /// case-insensitively.</summary>
   static PropertyInfo? FindSettingProp(Type t, string name) {
      foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
         var jn = p.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()?.Name;
         if ((jn != null && string.Equals(jn, name, StringComparison.OrdinalIgnoreCase)) ||
             string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            return p;
      }
      return null;
   }

   bool TryGetSettingByPath(string path, out object? value, out string err) {
      value = null;
      if (!ResolveSettingPath(path, out var owner, out var leaf, out err)) return false;
      value = leaf!.GetValue(owner);
      return true;
   }

   /// <summary>Set a leaf settings value from a string, coercing to the property's
   /// type (bool / int / float / string / enum). Returns the stringified new value.</summary>
   bool TrySetSettingByPath(string path, string raw, out string newVal, out string err) {
      newVal = "";
      if (!ResolveSettingPath(path, out var owner, out var leaf, out err)) return false;
      var pt = leaf!.PropertyType;
      try {
         object converted;
         if (pt == typeof(bool)) {
            string r = raw.Trim().ToLowerInvariant();
            converted = r is "1" or "true" or "on" or "yes";
         } else if (pt == typeof(int)) {
            converted = (int)DevFloat(raw);
         } else if (pt == typeof(float)) {
            converted = DevFloat(raw);
         } else if (pt == typeof(string)) {
            converted = raw;
         } else if (pt.IsEnum) {
            converted = Enum.Parse(pt, raw, true);
         } else {
            err = $"unsupported setting type {pt.Name} for '{path}'"; return false;
         }
         leaf.SetValue(owner, converted);
         newVal = converted.ToString() ?? "";
         return true;
      } catch (Exception ex) {
         err = $"failed to set '{path}': {ex.Message}"; return false;
      }
   }

   /// <summary>Parse a screenshot downsample_to option. Absent → default 640x360
   /// (half of the 1280x720 default render — small, readable, and a consistent
   /// fraction of the frame). "full" / "none" / "0" → no downsample (native render
   /// size). "WxH" → that size.</summary>
   static (int w, int h) ParseDownsample(string? s) {
      if (string.IsNullOrEmpty(s)) return (640, 360);
      s = s.ToLowerInvariant();
      if (s == "full" || s == "none" || s == "0") return (0, 0);
      var p = s.Split('x');
      if (p.Length == 2 && int.TryParse(p[0], out int w) && int.TryParse(p[1], out int h))
         return (w, h);
      return (640, 360);
   }

   /// <summary>Snapshot of live game state as a JSON object (the result payload).</summary>
   string BuildDevStateJson() {
      int undead = 0, human = 0, animal = 0;
      for (int i = 0; i < _sim.Units.Count; i++) {
         switch (_sim.Units[i].Faction) {
            case Faction.Undead:
               undead++;
               break;
            case Faction.Human:
               human++;
               break;
            case Faction.Animal:
               animal++;
               break;
         }
      }

      int ni = _sim.NecromancerIndex;
      string necro = ni >= 0
         ? $"{{\"index\":{ni},\"x\":{_sim.Units[ni].Position.X:F2},\"y\":{_sim.Units[ni].Position.Y:F2}," +
           $"\"mana\":{_sim.NecroState.Mana:F1},\"maxMana\":{_sim.NecroState.MaxMana:F1}}}"
         : "null";

      var ci = System.Globalization.CultureInfo.InvariantCulture;
      return "{" +
             $"\"worldLoaded\":{(_gameWorldLoaded ? "true" : "false")}," +
             $"\"menuState\":\"{_menuState}\"," +
             $"\"paused\":{(_paused ? "true" : "false")}," +
             $"\"pauseSources\":\"{_clock.PauseSources}\"," +
             $"\"worldRunning\":{(_clock.WorldRunning ? "true" : "false")}," +
             $"\"timeScale\":{_timeScale.ToString("F2", ci)}," +
             // World age (resets per game) — was the never-resetting visual clock.
             $"\"gameTime\":{_sim.GameTime.ToString("F2", ci)}," +
             $"\"visualTime\":{_clock.VisualTime.ToString("F2", ci)}," +
             $"\"units\":{_sim.Units.Count}," +
             $"\"undead\":{undead},\"human\":{human},\"animal\":{animal}," +
             $"\"camera\":{{\"x\":{_camera.Position.X.ToString("F2", ci)},\"y\":{_camera.Position.Y.ToString("F2", ci)},\"zoom\":{_camera.Zoom.ToString("F2", ci)}}}," +
             $"\"necromancer\":{necro}" +
             "}";
   }

   /// <summary>Resolve a dev unit selector to a list of indices into _sim.Units.
   /// Accepts: "all"/"*"; "necro"/"necromancer"; a faction ("undead"/"human"/
   /// "animal"); a bare integer index; "id:&lt;n&gt;" for a unit Id; otherwise a
   /// UnitDef id or UnitType name (case-insensitive). Faction / def / type / "all"
   /// selectors return only ALIVE units; explicit index / id return the unit
   /// regardless so it can still be inspected.</summary>
   List<int> DevResolveUnits(string token) {
      var result = new List<int>();
      token = (token ?? "").Trim();
      string lower = token.ToLowerInvariant();
      int count = _sim.Units.Count;

      switch (lower) {
         case "all":
         case "*":
            for (int i = 0; i < count; i++)
               if (_sim.Units[i].Alive)
                  result.Add(i);
            return result;
         case "necro":
         case "necromancer":
            if (_sim.NecromancerIndex >= 0) result.Add(_sim.NecromancerIndex);
            return result;
         case "undead":
            for (int i = 0; i < count; i++)
               if (_sim.Units[i].Alive && _sim.Units[i].Faction == Faction.Undead)
                  result.Add(i);
            return result;
         case "human":
            for (int i = 0; i < count; i++)
               if (_sim.Units[i].Alive && _sim.Units[i].Faction == Faction.Human)
                  result.Add(i);
            return result;
         case "animal":
            for (int i = 0; i < count; i++)
               if (_sim.Units[i].Alive && _sim.Units[i].Faction == Faction.Animal)
                  result.Add(i);
            return result;
      }

      if (lower.StartsWith("id:") && uint.TryParse(lower.Substring(3), out uint uid)) {
         for (int i = 0; i < count; i++)
            if (_sim.Units[i].Id == uid) {
               result.Add(i);
               break;
            }

         return result;
      }

      if (int.TryParse(token, out int idx)) {
         if (idx >= 0 && idx < count) result.Add(idx);
         return result;
      }

      // Match by UnitDef id or UnitType name (alive only).
      for (int i = 0; i < count; i++) {
         if (!_sim.Units[i].Alive) continue;
         if (_sim.Units[i].UnitDefID.Equals(token, StringComparison.OrdinalIgnoreCase) ||
             _sim.Units[i].Type.ToString().Equals(token, StringComparison.OrdinalIgnoreCase))
            result.Add(i);
      }

      return result;
   }

   /// <summary>One unit serialized as a compact JSON object (the shape returned by
   /// the `units` / `unit` dev commands).</summary>
   string DevUnitJson(int i) {
      var ci = System.Globalization.CultureInfo.InvariantCulture;
      var u = _sim.Units[i];
      return "{" +
             $"\"idx\":{i}," +
             $"\"id\":{u.Id}," +
             $"\"type\":\"{u.Type}\"," +
             $"\"def\":{System.Text.Json.JsonSerializer.Serialize(u.UnitDefID ?? "")}," +
             $"\"faction\":\"{u.Faction}\"," +
             $"\"ai\":\"{u.AI}\"," +
             $"\"x\":{u.Position.X.ToString("F2", ci)},\"y\":{u.Position.Y.ToString("F2", ci)}," +
             $"\"hp\":{u.Stats.HP},\"maxHp\":{u.Stats.MaxHP}," +
             $"\"poison\":{u.PoisonStacks}," +
             $"\"mana\":{u.Mana.ToString("F1", ci)},\"maxMana\":{u.MaxMana.ToString("F1", ci)}," +
             $"\"alive\":{(u.Alive ? "true" : "false")}," +
             $"\"detectionRange\":{u.DetectionRange.ToString("F1", ci)}," +
             $"\"inCombat\":{(u.InCombat ? "true" : "false")}," +
             $"\"incapActive\":{(u.Incap.Active ? "true" : "false")}," +
             $"\"recovering\":{(u.Incap.Recovering ? "true" : "false")}," +
             $"\"recoverTimer\":{u.Incap.RecoverTimer.ToString("F2", ci)}," +
             $"\"recoverAnim\":\"{u.Incap.RecoverAnim}\"," +
             $"\"archetype\":{u.Archetype}," +
             $"\"alertState\":{u.AlertState},\"alertTarget\":{u.AlertTarget}," +
             $"\"routine\":{u.Routine},\"sub\":{u.Subroutine}," +
             $"\"panic\":{u.PanicTimer.ToString("F2", ci)}," +
             $"\"vel\":{u.Velocity.Length().ToString("F2", ci)}," +
             $"\"prefVel\":{u.PreferredVel.Length().ToString("F2", ci)}," +
             $"\"huntTgt\":{u.WolfHuntTargetId},\"huntPhase\":{u.WolfHuntPhase},\"huntTimer\":{u.WolfHuntTimer.ToString("F1", ci)}," +
             $"\"squadId\":{u.SquadId}," +
             $"\"villageId\":{u.VillageId}," +
             $"\"aggroScale\":{u.AggroRangeScale.ToString("F2", ci)},\"herdT\":{u.HerdedTimer.ToString("F1", ci)}," +
             $"\"anim\":\"{(_unitAnims.TryGetValue(u.Id, out var _adbg) ? _adbg.Ctrl.CurrentState.ToString() : "?")}\"," +
             $"\"facing\":{u.FacingAngle.ToString("F0", ci)}," +
             $"\"velAngle\":{(u.Velocity.LengthSq() > 0.01f ? (MathF.Atan2(u.Velocity.Y, u.Velocity.X) * 180f / MathF.PI) : 0f).ToString("F0", ci)}," +
             $"\"engaged\":{(u.EngagedTarget.IsUnit ? "true" : "false")}," +
             $"\"target\":{(u.Target.IsUnit ? "true" : "false")}," +
             $"\"combatSpeed\":{u.Stats.CombatSpeed.ToString("F2", ci)}," +
             $"\"carryingCorpse\":{u.CarryingCorpseID}," +
             $"\"corpsePhase\":{u.CorpseInteractPhase}" +
             "}";
   }

   /// <summary>Parse a batch script (JSON array of step objects) into DevScriptSteps.
   /// Returns null + an error message on malformed input.</summary>
   List<Necroking.Dev.DevScriptStep>? ParseDevScript(string rawJson, out string err) {
      err = "";
      try {
         using var doc = System.Text.Json.JsonDocument.Parse(rawJson);
         if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) {
            err = "script must be a JSON array";
            return null;
         }

         var steps = new List<Necroking.Dev.DevScriptStep>();
         foreach (var el in doc.RootElement.EnumerateArray()) {
            if (el.ValueKind != System.Text.Json.JsonValueKind.Object) {
               err = "each step must be an object";
               return null;
            }

            if (el.TryGetProperty("wait", out var w)) {
               steps.Add(new Necroking.Dev.DevScriptStep { WaitSimSecs = (float)w.GetDouble() });
               continue;
            }

            if (el.TryGetProperty("wait_real", out var wr)) {
               steps.Add(new Necroking.Dev.DevScriptStep { WaitRealSecs = (float)wr.GetDouble() });
               continue;
            }

            if (el.TryGetProperty("wait_frames", out var wf)) {
               steps.Add(new Necroking.Dev.DevScriptStep { WaitFrames = wf.GetInt32() });
               continue;
            }

            // "shot" sugar → a screenshot command carrying the same opts. Screenshot
            // opts may sit at the top level ({shot:"x", no_ui:true}) or in an
            // explicit opts:{} object; both reach the command's Opts (opts:{} wins).
            if (el.TryGetProperty("shot", out var shot)) {
               var cmd = Necroking.Dev.DevCommand.FromElement(el, "screenshot");
               cmd.Cmd = "screenshot";
               cmd.Args = new[] { shot.GetString() ?? "shot" };
               foreach (var p in el.EnumerateObject()) {
                  if (p.Name is "shot" or "cmd" or "args" or "opts") continue;
                  if (!cmd.Opts.ContainsKey(p.Name))
                     cmd.Opts[p.Name] = Necroking.Dev.DevCommand.NormalizeOptValue(p.Value);
               }
               steps.Add(new Necroking.Dev.DevScriptStep { Cmd = cmd });
               continue;
            }

            var dc = Necroking.Dev.DevCommand.FromElement(el);
            if (string.IsNullOrEmpty(dc.Cmd)) {
               err = "step has no cmd/wait";
               return null;
            }

            if (dc.Cmd == "batch") {
               err = "nested batch is not allowed";
               return null;
            }

            steps.Add(new Necroking.Dev.DevScriptStep { Cmd = dc });
         }

         return steps;
      }
      catch (Exception ex) {
         err = ex.Message;
         return null;
      }
   }

   /// <summary>Status of a batch job as JSON (the `job` command's payload). Reports
   /// the active wait so a poller can see WHY the job isn't advancing — in particular
   /// a sim-seconds wait while the game is paused never elapses (blockedByPause).</summary>
   string DevJobStatusJson(Necroking.Dev.DevJob job) {
      var ci = System.Globalization.CultureInfo.InvariantCulture;
      string wait = "";
      if (!job.Done) {
         if (job.WaitFrames > 0) wait = $"\"waitFrames\":{job.WaitFrames},";
         else if (job.WaitSim > 0f) wait = $"\"waitSim\":{job.WaitSim.ToString("F2", ci)},";
         else if (job.WaitReal > 0f) wait = $"\"waitReal\":{job.WaitReal.ToString("F2", ci)},";
         // A sim wait can't elapse while the sim is frozen (pause, full-screen
         // editor, no world) — surface it so a poller doesn't spin forever.
         if (job.WaitSim > 0f && (_clock.Paused || EditorActive || !_gameWorldLoaded))
            wait += $"\"simWaitBlockedBy\":\"{(_clock.Paused ? "pause" : EditorActive ? "editor" : "no_world")}\",";
      }
      return "{" +
             $"\"id\":\"{job.Id}\"," +
             $"\"done\":{(job.Done ? "true" : "false")}," +
             $"\"canceled\":{(job.Canceled ? "true" : "false")}," +
             $"\"step\":{job.Cursor},\"total\":{job.Steps.Count}," +
             wait +
             $"\"results\":[{string.Join(",", job.Results)}]" +
             "}";
   }

   /// <summary>Advance the active batch script. Mirrors a scenario's OnTick: it
   /// burns down the current wait (sim seconds, real seconds, or frames), then
   /// runs as many command steps as it can until the next wait or an async command
   /// (a screenshot, which only finishes once Draw writes the PNG).</summary>
   void UpdateDevScript(float simDt, float realDt) {
      var job = _devJob;
      if (job == null || job.Done) return;

      // Drain an in-flight async command (screenshot) before doing anything else.
      if (job.InFlight != null) {
         if (!job.InFlight.Result.IsCompleted) return; // PNG not written yet
         job.Results.Add(job.InFlight.Result.Result);
         job.InFlight = null;
      }

      // Burn down the active wait, if any. A frame/sim/real wait blocks until spent.
      if (job.WaitFrames > 0) {
         job.WaitFrames--;
         return;
      }

      if (job.WaitSim > 0f) {
         job.WaitSim -= simDt;
         if (job.WaitSim > 0f) return;
         job.WaitSim = 0f;
      }

      if (job.WaitReal > 0f) {
         job.WaitReal -= realDt;
         if (job.WaitReal > 0f) return;
         job.WaitReal = 0f;
      }

      // Run forward until we hit a wait or launch an async command.
      while (job.Cursor < job.Steps.Count) {
         var step = job.Steps[job.Cursor++];
         if (step.WaitFrames > 0) {
            job.WaitFrames = step.WaitFrames;
            return;
         }

         if (step.WaitSimSecs > 0f) {
            job.WaitSim = step.WaitSimSecs;
            return;
         }

         if (step.WaitRealSecs > 0f) {
            job.WaitReal = step.WaitRealSecs;
            return;
         }

         if (step.Cmd != null) {
            ExecuteDevCommand(step.Cmd);
            if (!step.Cmd.Result.IsCompleted) {
               job.InFlight = step.Cmd;
               return;
            } // e.g. screenshot

            job.Results.Add(step.Cmd.Result.Result);
         }
      }

      job.Done = true;
   }

    /// <summary>Dev-server screenshot: take it from the just-rendered backbuffer
    /// (before Present) and complete the pending HTTP command with the file path.
    /// Called from every Draw branch so menu screenshots work too.</summary>
    internal void CompletePendingDevScreenshot()
    {
        if (_pendingDevScreenshot == null) return;
        bool okShot = ScenarioScreenshot.TakeScreenshot(GraphicsDevice, _pendingDevScreenshot, _devShotW, _devShotH);
        string shotPath = $"log/screenshots/{_pendingDevScreenshot}.png";
        _pendingDevScreenshotCmd?.Complete(okShot
            ? Necroking.Dev.DevServer.Ok(shotPath)
            : Necroking.Dev.DevServer.Error($"screenshot failed: {shotPath}"));
        _pendingDevScreenshot = null;
        _pendingDevScreenshotCmd = null;
        // Suppression only applies to the one captured frame.
        _devShotNoUi = false;
        _devShotNoGround = false;
    }
}
