# Initialization / Startup / Loading Anti-Patterns
*Anti patterns for game startup, new-game / map load, save & data-file round-trips, and asset /
GPU init. The generic (everywhere) anti patterns live in [anti-patterns.md](anti-patterns.md);
the other subsystem counterparts are [anti-patterns-rendering.md](anti-patterns-rendering.md),
[anti-patterns-ui.md](anti-patterns-ui.md), and [anti-patterns-gameplay.md](anti-patterns-gameplay.md).
Same discipline: egregious ones get refactored on sight and told to the main claude; regular
ones get logged in [anti-patterns-list.md](anti-patterns-list.md) and raised as fix candidates
when relevant.*

Each entry was paid for by a real bug (commit hashes cited — grep them for the full story).
Deep references: [save-load.md](save-load.md), [data-registries.md](data-registries.md),
[asset-management.md](asset-management.md), [game1-partials.md](game1-partials.md) (StartGame /
GameSession), [../standard_patterns.md](../standard_patterns.md). This is the "what not to do" index.

> **The meta-shape:** loading is where "state from last time" and "state from another file" leak
> in. Most of these are one of two failure modes — **state not reset/disposed on load** (leaks
> into the next session or its save file), or **the wrong thing serialized / read** (derived,
> foreign, partial, or default state persisted as truth). Both are silent until a reload or a
> round-trip corrupts something.

---

## Per-session / per-map state & resources

### **Egregious Anti Pattern**: a load path that *appends* to per-session state without clearing it first
When load appends and never clears, every reload stacks another copy — and if a save then writes
the grown collection, you get an unbounded growth-and-persist loop that corrupts the data file.
- `d0f8ea1`: `WallSystem._defs` was never cleared on reload while `MapData.Load` **appends** the
  map's wall defs every `StartGame`. A local test map reached **5.8M junk wall defs / 433 MB**:
  "load appends → editor save writes the grown list → next load appends again."
- `17dc82b`: the grass grid had the same shape — a 200-tile map absorbed default.json's 34 MB
  grass layer through repeated load-append-then-save.
**Instead:** clear per-session/per-map state at the top of the load (mirror `_zoneSystem.Clear()`),
or own it in a disposable session (below). The signature to watch for: `Load()` that `.Add()`s
into a long-lived collection with no matching clear, especially when a `Save()` re-serializes
that same collection.

### **Anti Pattern**: conditional reset — only clearing state when the incoming data *has* that section
`17dc82b`: grass fields were overwritten only when the loaded map JSON **had** a `grassMap`
section, so loading a grassless map kept the previous map's 5120×5120 grid alive (and the next
Save Map baked it into that map's file at foreign coordinates).
**Instead:** reset unconditionally at the top of load. A section-less load must still clear the
old section — never let "no new data" mean "keep the stale data."

### **Anti Pattern**: GPU / native resources orphaned on reload instead of disposed
`d0f8ea1` disposed per-load GPU textures orphaned every reload (ground textures, env textures +
corruption variants, flipbooks, the ground vertex-map texture). `f9a9c1f` introduced
`GameSession` — the one recreatable owner whose `Dispose()` frees GPU resources — replacing
`StartGame`'s ad-hoc `ClearObjects/ClearDefs/ClearTypes` dance.
**Instead:** own per-game GPU/native resources in `GameSession` (disposed + recreated on
`StartGame`), not in scattered clear calls that each new resource must remember to join.

### **Anti Pattern**: reset/clear coverage duplicated per entry path with different subsets
`c54b712`: `StartGame` and `StartScenario` each cleaned a **different subset** of state, so
scenario→map and back-to-back scenarios leaked the previous run's world (triggers, villages,
inventory, roads, zones, grass, a GPU vertex texture, plus a stale `_activeScenario` bouncing the
new map back to the menu). One `ResetWorldState()` superset now serves both entry paths.
**Instead:** one shared reset core called by every world-entry path — whatever a per-path clear
forgets is exactly what leaks into the next session. This is the init-side twin of the
session-recreate asymmetry in [anti-patterns-gameplay.md](anti-patterns-gameplay.md).

---

## Serialization & data round-trips

### **Anti Pattern**: persisting derived / runtime / foreign state instead of deriving it on load
- `41b0ca7`: saves stored the *secondary effects* of learned talents (unlocked
  potions/buildings/summons) — now re-derived on load from the learned-talent set (the single
  source of truth), replaying only pure/idempotent unlock effects.
- `8742ad3`: `WeaponPoints` (owned by `weapon_points.json`) was serialized into `units.json` →
  `[JsonIgnore]` it; `d6b5f71` stripped the stale copies.
- `c507049`: runtime-spawned env objects were baked into the map JSON's `placedObjects`.
- `5bf2751`: default-valued fields bloated saved registry JSON.
**Instead:** serialize only authored state. Derive on load anything re-derivable from a source of
truth; `[JsonIgnore]` fields owned by another file or computed at runtime; omit default-valued
fields. Every field a save/data file writes should be something a human (or the source registry)
actually authored.

### **Egregious Anti Pattern**: saving over source data after a partial / failed load
`ecab63c`: a registry `Load()` that threw mid-array left the first N entries in memory and
returned false — and `Save()` happily persisted the truncation. **One bad `drainCloudColor`
object silently dropped 32 of 59 spells** through `--roundtrip-data`. Fix: catch per-entry (one
malformed entry no longer takes down everything after it), flag `LoadHadErrors`, and `Save`
refuses to write while the flag is set.
**Instead:** load must be per-entry resilient and flag partial failure; save must refuse while a
load-error flag is set. Never overwrite source data with an in-memory copy a failed load left
incomplete — the round-trip turns a transient parse error into permanent data loss.

### **Anti Pattern**: silent default-fallback at use-time instead of loud validation at load-time
`83779d2`: enum-string and magic-path fields silently fell back to a default on a typo (a mistyped
path requirement "silently removed the cast gate"). Now validated at load
(`RegistryBase.ValidateDef`) reporting field + bad value + allowed values; runtime reads cached
typed views (`CachedEnum<T>`), never `Enum.TryParse` / switch-on-string in hot paths.
**Instead:** validate data fields at LOAD and fail loud with context (field, bad value, allowed
set), visible in `--roundtrip-data`; read parsed typed views at runtime. A silent default means a
typo changes behavior with no error. This is the standard in
[../standard_patterns.md](../standard_patterns.md) "Enum-string data fields".

---

## Startup ordering & bindings

### **Anti Pattern**: heavy startup blocking the first frame — and a save/exit handler firing before load finishes
`44525f3`: `Initialize`/`LoadContent` blocked the first frame ~8s (black window); the heavy work
(game data, atlas decode, GPU uploads, anim meta, shaders, renderers, wiring, editors) was
re-queued as labeled steps run one-per-frame behind a loading screen. The embedded data-loss
trap: the `Exiting` handler had to be guarded to **skip saving settings/weather/spellbar while
still loading — otherwise it overwrites the user's files with defaults**.
**Instead:** break startup into visible steps rather than one synchronous block; and guard every
save/exit/autosave path on "fully loaded," so a quit (or crash handler) during startup can't
write default state over the user's real files.

### **Anti Pattern**: one-time init placed in an event/open handler (one of N entry paths)
`2f19f1d`: the map editor's last-open tab was restored inside the HUD-button and pause-menu open
handlers, so opening via the **F11** hotkey (a third path) skipped the restore and showed the
default tab. Moved the single `RestoreTabFromSettings()` to init, right after the editor receives
its `GameData`, so state is correct before any open path runs.
**Instead:** one-time setup lives at a single init point, not in one (or some) of several entry
handlers where another path will skip it. The init twin of the spawn-path / UI-hotkey asymmetry.

### **Anti Pattern**: a stringly-typed shader / asset / content binding that silently no-ops when the name is wrong
`1723029`: a find/replace renamed the shader-parameter string `"WorldSize"` → `"Game1.WorldSize"`.
No uniform by that name exists, so the `SetValue` was a **silent no-op**, `WorldSize` stayed 0,
the shader's `1/WorldSize` blew every tilemap UV to infinity, and all painted terrain (water,
dirt, roads) collapsed to grass.
**Instead:** bind uniforms / assets / content keys by a centralized const, not a bare string
literal that produces no error when misspelled; verify the binding actually resolved (non-null /
logged). Never let a bulk rename touch a binding string blind — a wrong name is a silent
default-value bug, not a crash. (Cross-links the "set every uniform every draw" rule in
[anti-patterns-rendering.md](anti-patterns-rendering.md).)

### **Anti Pattern**: load-once work ticked repeatedly (per-frame / per-file)
`ee79668`: `AbsorbCorpsesOnPiles` ran every 0.5s (O(piles×corpses)) → moved to a one-shot on map
load. `d0f8ea1`: `AnimMetaLoader` validated `effect_time` O(files×keys), dumping tens of thousands
of duplicate warnings into a never-cleared `asset.log` → validate **once** after all metas load.
**Instead:** run one-time load/setup work (pile absorb, validation, warning emission) once at
load, not on a per-frame tick or per-file loop. Repeated one-time work is a perf cost and, for
warnings, a log-spam source that buries real diagnostics.

---

## Related
- [anti-patterns.md](anti-patterns.md) — generic anti patterns + the delayed-execution / reset
  principles.
- [anti-patterns-rendering.md](anti-patterns-rendering.md) / [anti-patterns-ui.md](anti-patterns-ui.md)
  / [anti-patterns-gameplay.md](anti-patterns-gameplay.md) — the other subsystem counterparts
  (gameplay's session-recreate asymmetry is the runtime twin of #4 here).
- [save-load.md](save-load.md) — save schema, StartGame/GameSession per-game reset.
- [data-registries.md](data-registries.md) — `RegistryBase` Load/Save, `ValidateDef`, `JsonFile`,
  the enum-string typed-view standard.
- [asset-management.md](asset-management.md) — data-file editing rules (JSON round-trip safety).
- [../known-platform-bugs.md](../known-platform-bugs.md) — framework/OS init bugs (GPU routing,
  focus gate) and their workarounds.
