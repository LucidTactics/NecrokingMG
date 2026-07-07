# Dossier: Texture/Asset Load-and-Cache Patterns

Concept judged: texture load + cache duplication across the Necroking codebase.
Verified against source on 2026-07-06. Repo: c:/Nightfall/NecrokingMG.

## Context: what is already canonical

`Render/TextureUtil.cs` **is** the documented single entry point for texture loading
(docs/locate-behavior/render.md:133 — "Load-time premultiply (the ONLY texture entry
points)"). Every loader in the game funnels through `TextureUtil.LoadPremultiplied` or
`DecodePngPremultiplied` (18 call sites, verified by grep; no `Content.Load<Texture2D>`
anywhere). `Render/AtlasCache.cs` is a *disk pixel cache* for the startup atlas decode —
a different layer, not a runtime texture cache, and not a candidate home for one.

So the duplication is NOT "multiple texture loaders." It is one level up: **the
get-or-load memoization idiom around LoadPremultiplied is hand-rolled at least six
times**, plus an editor/runtime mirror of the widget texture+nine-slice cache stack.

---

## Finding 1 — Path→Texture2D get-or-load idiom, 6 hand-rolled copies
**Verdict: CONSOLIDATE — severity medium**

Same 10–15 line pattern: `dict.TryGetValue(path) → GamePaths.Resolve →
File.Exists → TextureUtil.LoadPremultiplied → store (sometimes store null on
failure) → return`. Instances:

| Site | File:line | Negative cache | Resolve before load | Logs errors |
|---|---|---|---|---|
| `Game1.GetItemTextureByPath` | Necroking/Game1.cs:4270 | yes (null) | yes | no |
| `Game1.GetItemTexture` (DEAD — see Finding 2) | Necroking/Game1.cs:4283 | yes | yes | no |
| `RuntimeWidgetRenderer.GetOrLoadTexture` | Necroking/UI/RuntimeWidgetRenderer.cs:752 | no | resolves for File.Exists but passes **unresolved** path to load | yes |
| `UIEditorWindow.GetOrLoadTexture` | Necroking/Editor/UIEditorWindow.cs:1025 | no | passes **resolved** path | no |
| `TextureFileBrowser.LoadPreviewTexture` | Necroking/Editor/TextureFileBrowser.cs:394 | no | resolves for exists, loads unresolved | no |
| `GrassTuftRenderer.SetGrassTypes` (inline) | Necroking/Render/GrassTuftRenderer.cs:106 | no | no | yes |
| `EnvironmentSystem.GetOrLoadOverrideTexture` | Necroking/World/EnvironmentSystem.cs:1577 | yes | yes | yes (startup channel) |

The drift columns show this has *already* diverged: inconsistent negative caching
(a missing file is re-probed every frame in the no-negative-cache versions),
inconsistent error logging, and resolved-vs-unresolved path passed to the loader
(works only because `LoadPremultiplied` re-resolves internally — a silent coupling).

### Proposed canonical design
New `Necroking/Render/TextureCache.cs` (instance class — caller owns lifetime, per
CLAUDE.md "shared component owns mechanics, caller owns data"):

```csharp
public sealed class TextureCache(GraphicsDevice device, string? logChannel = null)
{
    public Texture2D? GetOrLoad(string path);  // resolve → exists → LoadPremultiplied;
                                               // caches null on failure; optional log
    public void DisposeAll();                  // for editor Shutdown paths
}
```

Migrate: Game1 `_itemTextureCache`, RuntimeWidgetRenderer `_textures`,
UIEditorWindow `_textures`, TextureFileBrowser `_textureCache`, GrassTuftRenderer
`_texCache`, EnvironmentSystem `_overrideTextures`. (`TextureUtil._whitePixelCache`
stays — device-keyed, different shape. `GroundSystem` byPath at
World/GroundSystem.cs:375 is a local build-step dict, not a lazy cache — leave it.)

**Effort: M** (6 call sites, mechanical). **Risk: low** — behavior deltas are the
consolidation's point (uniform negative caching + logging); only watch that editor
Shutdown/dispose paths call `DisposeAll` where they disposed dicts before
(RuntimeWidgetRenderer.Shutdown:1256, UIEditorWindow ~1106).

---

## Finding 2 — Game1.GetItemTexture(itemId) is dead code
**Verdict: CONSOLIDATE (delete) — severity low**

The labeling pass flagged `GetItemTextureByPath` vs `GetItemTexture` as duplicates.
True, but stronger: `GetItemTexture(string itemId)` (Game1.cs:4283) has **zero call
sites** (grep `GetItemTexture\(` matches only its declaration). The live one,
`GetItemTextureByPath`, has exactly one caller (GameRenderer.World.cs:363, projectile
icons). Both also share `_itemTextureCache` with **mixed key spaces** (paths and item
ids in one dict) — harmless today only because the dead method never runs. UI item
icons actually go through `RuntimeWidgetRenderer.SetImage → GetOrLoadTexture`, not
this cache. Delete `GetItemTexture`; fold `GetItemTextureByPath` into Finding 1's
`TextureCache`. Effort: S. Risk: none.

---

## Finding 3 — Editor/runtime widget texture+nine-slice cache mirror
**Verdict: CONSOLIDATE (the cache layer only) — severity medium**

`UIEditorWindow` duplicates `RuntimeWidgetRenderer`'s entire resource-cache quadruplet
near-verbatim:

- `GetOrLoadTexture`: UI/RuntimeWidgetRenderer.cs:752 ↔ Editor/UIEditorWindow.cs:1025
- `GetOrLoadNineSlice`: UI/RuntimeWidgetRenderer.cs:767 ↔ Editor/UIEditorWindow.cs:1043
  (identical incl. the `NineSliceDef` field-copy block)
- harmonized-first `GetTexture`/`GetNineSlice`: UI/RuntimeWidgetRenderer.cs:1239–1254 ↔
  Editor/UIEditorWindow.Helpers.cs:17–34 (editor added null/empty + optional-prefix
  handling the runtime lacks — drift in progress)
- backing dicts `_textures/_nsInstances/_harmonizedTextures/_harmonizedNineSlices`:
  RuntimeWidgetRenderer.cs:34–39 ↔ UIEditorWindow.cs:328–332

**What NOT to merge:** the harmonize *bake* control flow is structurally different by
design — runtime does a batch three-phase parallel bake at load
(`GenerateHarmonizedTextures`, RuntimeWidgetRenderer.cs:1148) while the editor does
incremental per-layer live-edit with dispose-old/remove-on-clear semantics
(`ApplyHarmonize`, UIEditorWindow.Helpers.cs:192). Per CLAUDE.md, that variance is
control-flow, not data — keep separate. The per-pixel math is already shared in
`ColorHarmonizer` (`TransformPixels`/`HarmonizeTexture`), so the pixels can't diverge;
only the cache bookkeeping can.

### Proposed canonical design
`Necroking/UI/WidgetResourceCache.cs` owning the four dicts + lookups; callers supply
the nine-slice def resolver (their def list is live data in the editor):

```csharp
public sealed class WidgetResourceCache(GraphicsDevice device,
                                        Func<string, NineSliceDefData?> resolveNs)
{
    public Texture2D? GetTexture(string path, string cachePrefix = "");   // harmonized-first
    public NineSlice? GetNineSlice(string nsId, string cachePrefix = "");
    public Texture2D? GetOrLoadTexture(string path);                      // raw
    public NineSlice? GetOrLoadNineSlice(string nsId);
    public void StoreHarmonized(string cacheKey, Texture2D tex);          // bake writes
    public void RemoveHarmonized(string cacheKey, bool disposeOld);       // editor live-edit
    public void Shutdown();
}
```

Call-site categories: RuntimeWidgetRenderer draw paths (~12 uses), its bake phase C;
UIEditorWindow draw paths (~14 uses across UIEditorWindow.cs + Helpers.cs), its
`ApplyHarmonize`. Texture half sits on Finding 1's `TextureCache`.

**Effort: M.** **Risk: medium** — editor live-edit invalidation (dispose-old when
`old != sourceTex`) and NineSlice.Unload ownership must be preserved exactly; the
UI editor is heavily used, verify with /drive-game (open UI editor, tweak harmonize
sliders, confirm live preview + no disposed-texture crash).

---

## Finding 4 — SpriteAtlas twin load pipelines (sync vs split-phase)
**Verdict: CONSOLIDATE (thin re-plumb) — severity low**

`Render/SpriteAtlas.cs` has two entry pipelines:
- Sync: `Load` (:170) / `LoadExtension` (:211) — used only by
  Editor/UnitEditorWindow.cs:219–224.
- Split-phase (threaded startup): `ParseMetaOnly` (:249) + `SetTextureAndFinalize`
  (:257) and `ParseExtensionMeta` (:293) + `AttachExtensionTexture` (:279) — used by
  Game1.cs:1203–1279's parallel decode pipeline.

The labeler's "twin pipelines" is overstated in one respect: the heavy machinery
(`ParseMeta`, `FixupYOrigin`, `ComputeFrameBoundingBoxes`, `RescaleAllFrames`) is
**already shared** between both. What's duplicated is ~35 lines of list bookkeeping:
the clear/add/finalize sequence (`Load` body ≈ `SetTextureAndFinalize` body) and the
add/rollback sequence (`LoadExtension` ≈ `ParseExtensionMeta`+`AttachExtensionTexture`
with a *differently-ordered* rollback). A TextureIndex bookkeeping fix applied to one
twin and not the other = frames sampling the wrong sheet (visual corruption), which is
why this isn't zero-severity.

### Proposed merge
Express the sync methods in terms of the split-phase primitives (no API break):

```csharp
public bool Load(GraphicsDevice device, string png, string meta)
    => File.Exists(...) && ParseMetaOnly(meta)
       && SetTextureAndFinalizeFrom(TextureUtil.LoadPremultiplied(device, png));
public bool LoadExtension(GraphicsDevice device, string png, string meta)
    => IsLoaded && File.Exists(...) && ParseExtensionMeta(meta)
       && AttachFrom(TextureUtil.LoadPremultiplied(device, png));
```

(Ordering note: current `Load` loads the texture before parsing meta; parse-first is
equivalent for the caller — failure still returns false with no texture leaked, and it
drops `LoadExtension`'s manual back-out block since `ParseExtensionMeta` already rolls
back.) Call sites to re-verify: UnitEditorWindow atlas load, Game1 startup, any
`--scenario` that renders units. **Effort: S. Risk: medium** (startup + editor both
must keep working; the `_yFixupPending`/placeholder-heights dance is subtle — test
with a multi-sheet atlas, e.g. ZombieAnimals__1).

---

## Finding 5 — TextureUtil premultiply loop written 3×
**Verdict: CONSOLIDATE — severity low**

In Render/TextureUtil.cs the `RGB *= A/255` loop appears in:
- `PremultiplyAlpha(Texture2D)` :202 — in-place over `Color[]` from GetData
- `DecodePngPremultipliedStb` :72 — `byte[] → Color[]` convert+premultiply
- `DecodePngPremultipliedTimed` :98 — **verbatim copy** of the Stb body, inlined only
  to split decode-vs-pma stopwatch ticks (single caller: Game1.cs:1178 startup bench)

Same file, so drift risk is low, but the Timed copy is pure waste: the pma-split
question it answered is concluded (per perf memory, Skia path won and does zero
post-decode work). Merge: extract `PremultiplyBytesToColors(byte[] src, Color[] dst)`
used by Stb + Timed (or simpler: have Timed call `DecodePngPremultipliedStb` and
report a single stb tick count, losing the now-uninteresting split). Keep
`PremultiplyAlpha`'s in-place `Color[]` loop as a second tiny helper if desired —
its input shape differs (Color[] vs byte[]); forcing one loop over both would add
conversion cost on the hot decode path. **Effort: S. Risk: low** (benchmark-only
behavior change in the Timed variant).

---

## Finding 6 — AtlasDefs.FindExtensionSheets vs FindExtensionAnimMeta
**Verdict: KEEP_SEPARATE — severity low**

Core/AtlasDefs.cs:69 and :86. Both are `for (n=1;;n++) probe "{base}__{n}.{ext}"`
iterators, but they differ in yield shape (a `(png, meta)` pair requiring BOTH files
vs a single animationmeta path) — the variance is in the data contract, and the shared
part is a 6-line counting loop over `File.Exists`. A generic
`ProbeNumbered(baseName, Func<string,bool>, ...)` abstraction would be longer and less
readable than the two concrete iterators. Per CLAUDE.md: don't abstract structural
variance; nothing here will meaningfully diverge (the `__N` convention is fixed by
`IsExtensionName`). Labeler over-matched.

---

## Suggested execution order
1. Finding 2 (delete dead method) — free.
2. Finding 5 (TextureUtil internal) — free, same file.
3. Finding 1 (`Render/TextureCache`) — establishes the utility; add to
   memory/standard_patterns.md as the canonical path→texture cache.
4. Finding 3 (WidgetResourceCache on top of it).
5. Finding 4 (SpriteAtlas re-plumb) — separately, with multi-sheet visual verification.

Constraint check: nothing here touches Necroking/Net/; textures remain premultiplied
at load per the render.md convention; no draw-path/Material changes involved.
