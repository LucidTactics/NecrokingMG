# User settings — per-machine `user settings/settings.json`

The per-machine, **gitignored** settings/config the game persists (window mode, gameplay
toggles, editor tunables, weather, spell bar). Seeded from a shipped default on first run;
all runtime writes go to `user settings/` so the shipped `data/*.json` stops churning in git.

## Files

### `Necroking/Data/Registries/GameSettings.cs` — the schema + load/save
- **`GameSettingsData`** — the root object. One `[JsonPropertyName(...)]` property per
  **section** (`Bloom`, `Grass`, `Weather`, `DayNight`, `Display`, `General`, `Shadow`,
  `Horde`, `Combat`, `FogOfWar`, `Performance`, `Corruption`, `Tooltips`, `StartingInventory`).
  Each section is its own POCO class (e.g. `GeneralSettings`, `DisplaySettings`) with
  `[JsonPropertyName]` + a C# default value per field.
- **`GameSettingsData.Load(path)`** — deserializes via `Core.JsonFile.Load`, then assigns
  each section from the loaded object. **Newer whole-sections use `?? new XSettings()`** so an
  older file missing that section still gets defaults (see `Display`/`Corruption`/`Tooltips`).
- **`GameSettingsData.Save(path)`** — `Core.JsonFile.SaveIfChanged(path, this,
  Core.JsonDefaults.Indented)`. If-changed because the settings window auto-saves on a
  per-frame dirty flag; an unconditional write would rewrite ~60×/sec.

**Adding a new setting field:** add a `[JsonPropertyName("…")] public T Foo { get; set; } =
default;` to the **existing** section that fits (`GeneralSettings` is the catch-all for
misc/editor tunables — it already holds `editorScrollSpeed`, `wpRapidEdit`, etc.). Because
`Load` assigns whole sections wholesale, a field added to an existing class needs **no `Load`
change** — a missing key just deserializes to the C# default. Only a **brand-new section**
requires a new line in `Load` (with a `?? new()` fallback) and a new property on
`GameSettingsData`.

### `Necroking/Core/GamePaths.cs` — the paths + first-run seeding
- Constants: `UserSettingsDir = "user settings"`, `UserSettingsJson =
  "user settings/settings.json"`, `UserWeatherJson`, `UserSpellBarJson`; the shipped seeds
  are `SettingsJson = "data/settings.json"` (seed only — runtime never writes it), `WeatherJson`,
  `SpellBarJson`.
- **`SeededUserFile(userRelPath, defaultAbsPath)`** — returns the absolute user-copy path,
  copying the shipped default into `user settings/` on first run. This is how
  settings/weather/spellbar all bootstrap.

### `Necroking/Data/GameData.cs` — wiring
- `public GameSettingsData Settings { get; } = new();` — the single live instance.
  `_gameData.Settings.General.Foo` is how any setting is read at runtime.
- `Load(...)` calls `Settings.Load(GamePaths.SeededUserFile(UserSettingsJson,
  data/settings.json))`; weather + spell bar seed the same way. `GameData.Save(...)` also
  writes `Settings.Save(user settings/settings.json)`.

### Persistence sites in `Necroking/Game1.cs`
- **`Exiting += …`** (ctor, ~ln 861) — on clean exit: `_gameData.Settings.Save(UserSettingsJson)`
  + `_gameData.Weather.Save(UserWeatherJson)` + `SaveSpellBars()`. This is where the mirrored
  in-memory settings actually hit disk.
- **`SettingsWindow.cs`** (`_gameData.Settings.Save(_settingsJsonPath)`) — the in-game settings
  panel auto-saves on its own dirty flag while open.
- **The `Display` pattern** (canonical example of "persist a live UI/state change"):
  `ApplyWindowMode` writes `_gameData.Settings.Display.{Windowed,WindowedWidth,WindowedHeight}`
  into the in-memory settings on toggle; the `Exiting` handler does the disk write. Follow this
  for any new "remember how the user left X" setting — mutate `Settings.*` on change, let
  `Exiting` (or an explicit `Settings.Save`) persist.

### Weather / spell bar — same mechanism, separate files
- `weather.json` → `_gameData.Weather` (`WeatherRegistry`-ish), seeded/saved like settings via
  `UserWeatherJson`.
- `spellbar.json` → `SaveSpellBars()`/load in `Game1.cs` (~ln 1546), seeded from
  `data/spellbar.json` via `UserSpellBarJson`. Both are per-machine, never shared.

## Look / edit here when…
- **Adding a new persisted user setting / "remember X across restarts"** →
  add the field to the fitting section class in `GameSettings.cs` (usually `GeneralSettings`);
  read/write `_gameData.Settings.<Section>.<Field>` at runtime; rely on the `Exiting` handler
  (or call `_gameData.Settings.Save(GamePaths.Resolve(GamePaths.UserSettingsJson))`) to persist.
- **A setting isn't loading from an old settings.json** → check the `Load` assignment uses
  `?? new()` for that section.
- **Settings write to the wrong file / churn git** → runtime must target `UserSettingsJson`
  (the `user settings/` copy), never `data/settings.json` (seed only). Seeding via `SeededUserFile`.

## Related areas
- Id-keyed `data/*.json` registries (units/spells/items) and their save engine:
  [data-registries.md](data-registries.md) (different mechanism — `RegistryBase`, not
  `GameSettingsData`).
- Map editor (whose `ActiveTab` is a candidate for persisting here): [editor.md](editor.md).
