using System;
using System.Collections.Generic;
using System.Reflection;
using Necroking.Core;
using Necroking.Data;
using Necroking.Scenario;

namespace Necroking;

public partial class Game1 {
   void ExecuteDevCommand(Necroking.Dev.DevCommand c) {
      try {
         switch (c.Cmd) {
            case "ping":
               c.Complete(Necroking.Dev.DevServer.Ok("pong"));
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
               string mode = c.Args.Length >= 1 ? c.Args[0].ToLowerInvariant() : "toggle";
               bool want = mode switch { "on" => true, "off" => false, _ => !has };
               if (want != has) ToggleGodMode(ni);
               c.Complete(Necroking.Dev.DevServer.Ok($"godmode {(want ? "on" : "off")}"));
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
               if (c.Args.Length >= 3) _camera.Zoom = DevFloat(c.Args[2]);
               c.Complete(Necroking.Dev.DevServer.Ok(
                  $"camera ({_camera.Position.X},{_camera.Position.Y}) zoom={_camera.Zoom}"));
               break;
            }

            case "speed":
               if (c.Args.Length < 1) {
                  c.Complete(Necroking.Dev.DevServer.Error("speed needs: <n>"));
                  break;
               }

               _timeScale = DevFloat(c.Args[0]);
               _paused = false;
               c.Complete(Necroking.Dev.DevServer.Ok($"speed={_timeScale}"));
               break;

            case "pause":
               _paused = true;
               c.Complete(Necroking.Dev.DevServer.Ok("paused"));
               break;

            case "resume":
               _paused = false;
               c.Complete(Necroking.Dev.DevServer.Ok("resumed"));
               break;

            case "start_game": {
               string map = c.Args.Length > 0 ? c.Args[0] : "default";
               StartGame(map);
               _menuState = MenuState.None;
               c.Complete(Necroking.Dev.DevServer.Ok($"started map={map} units={_sim.Units.Count}"));
               break;
            }

            // Press a main-menu button. Mirrors the click handlers in Update so
            // there's one definition of what each button does.
            case "menu": {
               if (c.Args.Length < 1) {
                  c.Complete(Necroking.Dev.DevServer.Error("menu needs: <new_game|test_map|empty_test_map|scenarios|main_menu|quit>"));
                  break;
               }

               switch (c.Args[0].ToLowerInvariant()) {
                  case "new_game":
                  case "play":
                     StartGame();
                     c.Complete(Necroking.Dev.DevServer.Ok(
                        $"new game, menuState={_menuState}, units={_sim.Units.Count}"));
                     break;
                  case "test_map":
                     StartGame("testmap");
                     c.Complete(Necroking.Dev.DevServer.Ok($"test map, units={_sim.Units.Count}"));
                     break;
                  // Empty, grass-only map with a debug necromancer (all paths
                  // unlocked, +999 MaxMana). The right starting point for
                  // testing technical behavior — no map content to fight, all
                  // spells castable. See StartGame's "empty_test" branch.
                  case "empty_test_map":
                  case "empty_map":
                     StartGame("empty_test");
                     c.Complete(Necroking.Dev.DevServer.Ok($"empty test map, units={_sim.Units.Count}"));
                     break;
                  case "scenarios":
                     _menuState = MenuState.ScenarioList;
                     _scenarioScrollOffset = 0;
                     c.Complete(Necroking.Dev.DevServer.Ok("opened scenario list"));
                     break;
                  case "main_menu":
                  case "back":
                     _menuState = MenuState.MainMenu;
                     _paused = false;
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

            case "screenshot": {
               string name = c.Opt("name") ?? (c.Args.Length > 0 ? c.Args[0] : "devshot");
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
                     "main_menu", "scenarios", "game", "pause", "settings",
                     "unit_editor", "spell_editor", "map_editor", "ui_editor", "item_editor"
                  },
                  tabs = new {
                     settings = Necroking.Editor.SettingsWindow.TabIds,
                     map_editor = Enum.GetNames(typeof(Necroking.Editor.MapEditorTab)),
                     ui_editor = Enum.GetNames(typeof(Necroking.Editor.UIEditorTab)),
                  },
                  overlays = new[] { "inventory", "character_stats", "skill_book", "grimoire", "character_sheet" },
                  current = _menuState.ToString(),
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

            // Show/hide an in-game overlay (inventory, stats, skill book, ...).
            case "overlay": {
               if (c.Args.Length < 1) {
                  c.Complete(Necroking.Dev.DevServer.Error(
                     "overlay needs: <name> [open|close|toggle] — run 'panels' for the list"));
                  break;
               }

               string action = c.Args.Length >= 2 ? c.Args[1].ToLowerInvariant() : "toggle";
               string? r = SetOverlay(c.Args[0], action);
               if (r == null) {
                  c.Complete(Necroking.Dev.DevServer.Error(
                     $"unknown overlay: {c.Args[0]} — run 'panels' for the list"));
                  break;
               }

               c.Complete(Necroking.Dev.DevServer.Ok(r));
               break;
            }

            // --- Editor click-leak test harness (weapon/armor/shield sub-editor) ---
            case "ed_weapon_sub":
                _menuState = MenuState.UnitEditor;
                _paused = false;
                _unitEditor.DevEnsureSelection();
                _unitEditor.OpenWeaponSubEditor();
                _unitEditor.DevUnsavedChanges = false;
                c.Complete(Necroking.Dev.DevServer.Ok(_unitEditor.DevSubState()));
                break;
            case "ed_state":
                c.Complete(Necroking.Dev.DevServer.Ok(_unitEditor.DevSubState()));
                break;
            case "ed_mouse": {
                // ed_mouse <x> <y> <down|up> — persistent synthetic editor mouse (held until next ed_mouse/ed_mouse_off)
                if (c.Args.Length < 3 || !int.TryParse(c.Args[0], out int cx) || !int.TryParse(c.Args[1], out int cy)) {
                    c.Complete(Necroking.Dev.DevServer.Error("ed_mouse <x> <y> <down|up>"));
                    break;
                }
                _devMouseActive = true; _devMouseX = cx; _devMouseY = cy; _devMouseDown = c.Args[2] == "down";
                c.Complete(Necroking.Dev.DevServer.Ok($"editor mouse {(_devMouseDown ? "DOWN" : "UP")} @ {cx},{cy}"));
                break;
            }
            case "ed_mouse_off":
                _devMouseActive = false;
                c.Complete(Necroking.Dev.DevServer.Ok("editor mouse override off"));
                break;

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
                  SpawnUnit(c.Args[0], new Vec2(bx + i, by));
                  idxs.Add(_sim.Units.Count - 1);
               }
               c.Complete(Necroking.Dev.DevServer.OkRaw(
                  $"{{\"def\":{System.Text.Json.JsonSerializer.Serialize(c.Args[0])},\"count\":{count},\"indices\":[{string.Join(",", idxs)}]}}"));
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

            // Assign a unit to a grave (nearest unoccupied empty_grave if idx omitted):
            // window.dev('assign_worker',[unitId]) or window.dev('assign_worker',[unitId, graveObjIdx])
            case "assign_worker": {
               if (c.Args.Length < 1) { c.Complete(Necroking.Dev.DevServer.Error("assign_worker needs: <unitId> [graveObjIdx]")); break; }
               uint uid = (uint)DevFloat(c.Args[0]);
               int graveIdx;
               if (c.Args.Length >= 2) graveIdx = (int)DevFloat(c.Args[1]);
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

            // One-shot demo scene: grave + pile + mushrooms + a skeleton worker assigned.
            // window.dev('worker_demo')
            case "worker_demo": {
               Vec2 nb = _sim.NecromancerIndex >= 0 ? _sim.Units[_sim.NecromancerIndex].Position : new Vec2(2096, 1882);
               int graveDef = _envSystem.FindDef("empty_grave");
               int pileDef = _envSystem.FindDef("mushroom_pile");
               int mushDef = _envSystem.FindDef("deathcap");
               if (graveDef < 0 || pileDef < 0 || mushDef < 0) { c.Complete(Necroking.Dev.DevServer.Error("missing env defs (empty_grave/mushroom_pile/deathcap)")); break; }
               int graveObj = _envSystem.AddObject((ushort)graveDef, nb.X + 2, nb.Y);
               _envSystem.AddObject((ushort)pileDef, nb.X + 6, nb.Y);
               for (int m = 0; m < 8; m++)
                  _envSystem.AddObject((ushort)mushDef, nb.X + 9 + (m % 4) * 1.5f, nb.Y - 3 + (m / 4) * 2f);
               SpawnUnit("skeleton", new Vec2(nb.X + 2, nb.Y + 1));
               uint sid = _sim.Units[_sim.Units.Count - 1].Id;
               bool ok = _workerSystem.AssignWorker(sid, graveObj);
               c.Complete(Necroking.Dev.DevServer.OkRaw($"{{\"graveObj\":{graveObj},\"workerUnit\":{sid},\"assigned\":{(ok ? "true" : "false")}}}"));
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
               int Place(string id, float x, float y, float s = 1f) => _envSystem.AddObject((ushort)Def(id), x, y, s);

               // Buildings
               var graves = new List<int>();
               for (int g = 0; g < 6; g++)
                  graves.Add(Place("empty_grave", nb.X - 5 + (g % 3) * 1.6f, nb.Y - 3 + (g / 3) * 1.6f));
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
                  SpawnUnit("skeleton", new Vec2(nb.X - 5 + w, nb.Y));
                  uint wid = _sim.Units[_sim.Units.Count - 1].Id;
                  if (_workerSystem.AssignWorker(wid, graves[w])) assigned++;
               }
               c.Complete(Necroking.Dev.DevServer.Ok($"scene built: {graves.Count} graves, 7 buildings, sources + 8 corpses, {assigned} workers assigned"));
               break;
            }

            // Open the job board UI; optional arg expands a job's detail:
            // window.dev('ui_job_board',['make_potions'])
            case "ui_job_board": {
               EnsureInventoryUIsInitialized();
               if (!_jobBoardUI.IsVisible)
                  _jobBoardUI.Toggle(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
               c.Complete(Necroking.Dev.DevServer.Ok($"job board visible={_jobBoardUI.IsVisible}"));
               break;
            }
            // Force the board into a drag state for a screenshot: window.dev('ui_job_drag',['poison_berries', 320])
            case "ui_job_drag": {
               if (c.Args.Length < 2) { c.Complete(Necroking.Dev.DevServer.Error("ui_job_drag needs: <jobId> <mouseY>")); break; }
               EnsureInventoryUIsInitialized();
               if (!_jobBoardUI.IsVisible) _jobBoardUI.Toggle(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
               _jobBoardUI.DebugDrag(c.Args[0], (int)DevFloat(c.Args[1]));
               c.Complete(Necroking.Dev.DevServer.Ok($"dragging {c.Args[0]} at y={c.Args[1]}"));
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

            // Resolved locomotion profile (feet-lock vels, gait thresholds, legacy
            // vs new mode) + the def's overrides — for diagnosing gait/playback.
            case "locomotion": case "loco": {
               var lidxs = DevResolveUnits(c.Args.Length > 0 ? string.Join(" ", c.Args) : "necro");
               if (lidxs.Count == 0) { c.Complete(Necroking.Dev.DevServer.Error("no unit matched")); break; }
               var ldef = _gameData?.Units.Get(_sim.Units[lidxs[0]].UnitDefID);
               if (ldef == null) { c.Complete(Necroking.Dev.DevServer.Error("no def")); break; }
               var prof = Render.LocomotionProfile.FromUnit(ldef);
               var ci2 = System.Globalization.CultureInfo.InvariantCulture;
               bool hasCal = ldef.SpriteData?.Calibration != null;
               string ov(float? v) => v.HasValue ? v.Value.ToString("F2", ci2) : "null";
               c.Complete(Necroking.Dev.DevServer.OkRaw("{" +
                  $"\"def\":{System.Text.Json.JsonSerializer.Serialize(ldef.Id)}," +
                  $"\"isLegacyProfile\":{(prof.IsLegacy ? "true" : "false")}," +
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

            // Flag units so dying reanimates them — exercises the on-death composite reanim path.
            case "zombify": {   // window.dev('zombify',['human']) then kill -> full reanim effect
               var zidxs = DevResolveUnits(c.Args.Length > 0 ? string.Join(" ", c.Args) : "all");
               foreach (int i in zidxs) _sim.UnitsMut[i].ZombieOnDeath = true;
               c.Complete(Necroking.Dev.DevServer.Ok($"zombified {zidxs.Count} unit(s)"));
               break;
            }

            // Corpse-less composite reanim (the table-craft path): a green cloud builds at (x,y) and a
            // zombie rises from it — no world body to morph.
            case "reanim_at": {   // window.dev('reanim_at',[x,y,'skeleton'])
               if (c.Args.Length < 2) { c.Complete(Necroking.Dev.DevServer.Error("reanim_at needs: <x> <y> [defId]")); break; }
               string rdef = c.Args.Length > 2 ? c.Args[2] : "skeleton";
               QueueReanimRise(rdef, -1, "reanim_smoke",
                  posOverride: new Vec2(DevFloat(c.Args[0]), DevFloat(c.Args[1])), facingOverride: 90f, scaleOverride: 1f);
               c.Complete(Necroking.Dev.DevServer.Ok($"queued corpse-less reanim of '{rdef}'"));
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
               _gameData.Settings.Tooltips.ShowHoverHighlight = true;
               c.Complete(Necroking.Dev.DevServer.Ok($"hovering unit id={_devForceHoverUnitId} (variant {_hoverHighlightVariant})"));
               break;
            }

            // Set the hover-highlight style variant directly (0-11 = variants, 12 = off).
            case "hover_variant": {   // window.dev('hover_variant',[5])
               if (c.Args.Length < 1) { c.Complete(Necroking.Dev.DevServer.Error("hover_variant needs <0-12>")); break; }
               _hoverHighlightVariant = System.Math.Clamp((int)DevFloat(c.Args[0]), 0, 12);
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
                  c.Complete(Necroking.Dev.DevServer.Ok("walk_necro cancelled"));
                  break;
               }
               if (c.Args.Length < 2) {
                  c.Complete(Necroking.Dev.DevServer.Error("walk_necro needs: <x> <y>  (or 'clear')"));
                  break;
               }
               float wx = DevFloat(c.Args[0]), wy = DevFloat(c.Args[1]);
               _devWalkTarget = new Vec2(wx, wy);
               c.Complete(Necroking.Dev.DevServer.Ok($"necromancer walking to ({wx},{wy})"));
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

            // Set the F7 gameplay-debug overlay: 0=Off, 1=Horde, 2=Unit Info.
            // window.dev('gpdebug',['1'])  (no arg → Horde)
            case "gpdebug": {
               _gameplayDebugMode = c.Args.Length >= 1 ? (int)DevFloat(c.Args[0]) % 3 : 1;
               c.Complete(Necroking.Dev.DevServer.Ok($"gameplayDebugMode={_gameplayDebugMode}"));
               break;
            }

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
               var res = DispatchSpellCast(c.Args[0], necroIdx, 0, target, false);
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
               _sim.Projectiles.SpawnFireball(from, target, Faction.Undead, owner, dmg, radius, name);
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

            // Poll the active batch job. `job cancel` aborts it.
            case "job": {
               if (_devJob == null) {
                  c.Complete(Necroking.Dev.DevServer.Error("no batch job (run 'batch' first)"));
                  break;
               }

               if (c.Args.Length > 0 && c.Args[0].Equals("cancel", StringComparison.OrdinalIgnoreCase)) {
                  _devJob.Done = true;
                  _devJob.Canceled = true;
               }

               c.Complete(Necroking.Dev.DevServer.OkRaw(DevJobStatusJson(_devJob)));
               break;
            }

            // Discovery: list every dev command with a one-line signature.
            case "help":
            case "commands": {
               var cmds = new[] {
                  "ping", "state", "help",
                  "spawn <type> <x> <y>", "spawn_def <unitID> <x> <y> [count]",
                  "units [selector]", "unit <selector>", "combat_log [n]",
                  "damage <selector> <amount>", "kill <selector>", "remove <selector>",
                  "set_ai <selector> <AIBehavior>", "move <selector> <x> <y>",
                  "walk_necro <x> <y>  (or 'clear'; cancelled by any WASD press)",
                  "mark <selector|clear>", "unmark [selector]",
                  "set_hp <selector> <hp> [maxHp]", "set_mana <selector|necro> <mana> [maxMana]",
                  "set_necro_type <unitDefId>",
                  "cast <spellID> <x> <y>", "fireball <x> <y> [dmg] [radius] [name]",
                  "camera <x> <y> [zoom]", "speed <n>", "pause", "resume",
                  "start_game [map]", "menu <new_game|test_map|empty_test_map|scenarios|main_menu|quit>",
                  "screenshot [name]  opts:{no_ui,no_ground,downsample_to}",
                  "panels", "panel <name> [tab]", "tab <name>",
                  "overlay <name> [open|close|toggle]", "select <name|id|index>",
                  "batch  opts:{script:[{cmd,args,opts}|{wait:n}|{wait_real:n}|{wait_frames:n}|{shot:\"name\"}]}",
                  "job [cancel]",
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
            _paused = false;
            _gameWorldLoaded = false;
            return true;
         case "scenarios":
         case "scenario_list":
            _menuState = MenuState.ScenarioList;
            _scenarioScrollOffset = 0;
            return true;
      }

      // Everything below renders over a live world — load one if needed.
      if (!_gameWorldLoaded) StartGame();

      switch (name.ToLowerInvariant()) {
         case "game":
         case "gameplay":
         case "none":
            _menuState = MenuState.None;
            _paused = false;
            return true;
         case "pause":
         case "pause_menu":
         case "esc":
            _menuState = MenuState.PauseMenu;
            _paused = true;
            return true;
         case "settings":
         case "options":
            _menuState = MenuState.Settings;
            return true;
         case "unit_editor":
         case "uniteditor":
            _menuState = MenuState.UnitEditor;
            _paused = false;
            return true;
         case "spell_editor":
         case "spelleditor":
            _menuState = MenuState.SpellEditor;
            _paused = false;
            return true;
         case "map_editor":
         case "mapeditor":
            _menuState = MenuState.MapEditor;
            _paused = false;
            _mapEditor.SuppressClicksUntilRelease();
            return true;
         case "ui_editor":
         case "uieditor":
            EnsureUIEditorInitialized();
            _menuState = MenuState.UIEditor;
            _paused = false;
            return true;
         case "item_editor":
         case "itemeditor":
            _menuState = MenuState.ItemEditor;
            _paused = false;
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
   string? SetOverlay(string name, string action) {
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
            // No public Open(); Toggle() opens, Hide() closes.
            if (action == "close") _grimoireOverlay.Hide();
            else if (action == "open") {
               if (!_grimoireOverlay.IsVisible) _grimoireOverlay.Toggle();
            } else _grimoireOverlay.Toggle();

            return $"grimoire visible={_grimoireOverlay.IsVisible}";

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
         default:
            return null;
      }
   }

   static float DevFloat(string s) =>
      float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

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
             $"\"timeScale\":{_timeScale.ToString("F2", ci)}," +
             $"\"gameTime\":{_gameTime.ToString("F2", ci)}," +
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
             $"\"mana\":{u.Mana.ToString("F1", ci)},\"maxMana\":{u.MaxMana.ToString("F1", ci)}," +
             $"\"alive\":{(u.Alive ? "true" : "false")}," +
             $"\"inCombat\":{(u.InCombat ? "true" : "false")}," +
             $"\"incapActive\":{(u.Incap.Active ? "true" : "false")}," +
             $"\"recovering\":{(u.Incap.Recovering ? "true" : "false")}," +
             $"\"recoverTimer\":{u.Incap.RecoverTimer.ToString("F2", ci)}," +
             $"\"recoverAnim\":\"{u.Incap.RecoverAnim}\"," +
             $"\"archetype\":{u.Archetype}," +
             $"\"routine\":{u.Routine},\"sub\":{u.Subroutine}," +
             $"\"panic\":{u.PanicTimer.ToString("F2", ci)}," +
             $"\"vel\":{u.Velocity.Length().ToString("F2", ci)}," +
             $"\"prefVel\":{u.PreferredVel.Length().ToString("F2", ci)}," +
             $"\"wolfPhase\":{u.WolfPhase}," +
             $"\"anim\":\"{(_unitAnims.TryGetValue(u.Id, out var _adbg) ? _adbg.Ctrl.CurrentState.ToString() : "?")}\"," +
             $"\"facing\":{u.FacingAngle.ToString("F0", ci)}," +
             $"\"velAngle\":{(u.Velocity.LengthSq() > 0.01f ? (MathF.Atan2(u.Velocity.Y, u.Velocity.X) * 180f / MathF.PI) : 0f).ToString("F0", ci)}," +
             $"\"engaged\":{(u.EngagedTarget.IsUnit ? "true" : "false")}," +
             $"\"target\":{(u.Target.IsUnit ? "true" : "false")}," +
             $"\"combatSpeed\":{u.Stats.CombatSpeed.ToString("F2", ci)}" +
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

            // "shot" sugar → a screenshot command carrying the same opts.
            if (el.TryGetProperty("shot", out var shot)) {
               var cmd = Necroking.Dev.DevCommand.FromElement(el, "screenshot");
               cmd.Cmd = "screenshot";
               cmd.Args = new[] { shot.GetString() ?? "shot" };
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

   /// <summary>Status of a batch job as JSON (the `job` command's payload).</summary>
   static string DevJobStatusJson(Necroking.Dev.DevJob job) {
      return "{" +
             $"\"id\":\"{job.Id}\"," +
             $"\"done\":{(job.Done ? "true" : "false")}," +
             $"\"canceled\":{(job.Canceled ? "true" : "false")}," +
             $"\"step\":{job.Cursor},\"total\":{job.Steps.Count}," +
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
    private void CompletePendingDevScreenshot()
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
