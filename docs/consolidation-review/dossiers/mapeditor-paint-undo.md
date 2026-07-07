# Dossier: Map editor paint brushes, undo actions, cost-field rebuilds

Unit: `mapeditor-paint-undo` · Judge pass 2026-07-06
Files: `Necroking/Editor/MapEditorWindow.cs`, `Necroking/World/TileGrid.cs`, `Necroking/World/EnvironmentSystem.cs`

---

## Finding 1 — `PaintObjects` is dead legacy code superseded by `PaintObjectsBatch`
**Verdict: CONSOLIDATE (delete) · Severity: medium · Effort: S · Risk: minimal**

Evidence:
- `PaintObjects` defined at `Necroking/Editor/MapEditorWindow.cs:2757` — **zero call sites** anywhere in the solution (grep for `PaintObjects(` finds only the definition; the sole brush call site is `PaintObjectsBatch(...)` at line 2350).
- `PaintObjectsBatch` (line 2612) is the same hex-grid jittered scatter loop (identical spacing math `spacing*0.866f`, jitter `spacing*0.25f`, circle test) but strictly better: weighted group pools, batch undo accumulation into `_batchPlacedObjects`, incremental `StampObjectCollisionAt`, auto-ground stamping, perf telemetry.
- `PaintObjects` still contains the O(total_objects)-per-candidate "too close" linear scan (lines 2793–2798) — the exact perf bug the batch version replaced — and records **no undo**, so reviving it would create a paint path whose strokes cannot be undone.

The labeler's "same intent, two implementations" claim is correct, but the resolution is deletion, not unification: this is a fully diverged legacy copy.

**Action:** delete `PaintObjects` (lines 2757–2817). No call sites to migrate. Verify with `dotnet build`.

---

## Finding 2 — `PaintWalls` vs `EraseWalls`: one brush loop, one branch
**Verdict: CONSOLIDATE · Severity: low · Effort: S · Risk: low**

Evidence:
- `PaintWalls` (`MapEditorWindow.cs:3131–3158`) and `EraseWalls` (`3163–3186`) are line-for-line identical: same `ScreenToWorld` → `SnapToWallGrid`, same circular brush loop, same `_wallStrokeOld?.TryAdd(idx, (types[idx], hps[idx]))` undo capture. The only difference is the final statement: paint does `SetWall(tx,ty,type)` unless `SelectedWallType==0` (in which case it already calls `ClearWall`); erase always calls `ClearWall`.
- So `EraseWalls(...)` ≡ `PaintWalls(...)` with the type forced to 0 — the erase branch **already exists** inside `PaintWalls`.
- Bonus duplication in the caller (`UpdateWallsTab`): the leftUp finalize block (3081–3100) and rightUp finalize block (3110–3128) are verbatim copies (push `UndoWallStroke`, `RebuildCostField`, `BakeWalls`).

Divergence risk is real if minor: any brush-shape/snap change made in one (e.g. brush falloff, `WallStep` change) silently misses the other, giving an eraser that can't reach tiles the painter placed.

**Merged API sketch:**
```csharp
private void PaintWalls(MouseState mouse, int screenW, int screenH, int wallType) // 0 = erase
// call sites: leftDown -> PaintWalls(..., SelectedWallType); rightDown -> PaintWalls(..., 0)
private void FinalizeWallStroke() // push UndoWallStroke, RebuildCostField, BakeWalls, _painting=false
```
Call sites to migrate: 2 brush calls + 2 finalize blocks, all inside `UpdateWallsTab`. Delete `EraseWalls`.

---

## Finding 3 — Undo action classes: base class already exists; variance is structural
**Verdict: KEEP_SEPARATE (with two micro-cleanups) · Severity: low**

Evidence (`MapEditorWindow.cs:331–538`): 14 concrete classes all deriving from the already-existing `private abstract class UndoAction { public abstract void Undo(); }` (line 331). Each subclass is 5–20 lines of pure data + one restore method. The labeler asked to "check base-class potential" — the base class **is already there**; the question is whether the subclasses can be merged further. They mostly can't:
- `UndoGroundStroke` / `UndoGrassStroke` / `UndoWallStroke` all look like "restore dict of old values", but the restore targets are structurally different: `Ground.SetVertex(vx,vy,v)` with a packed long key + `OnChanged` callback; raw `byte[]` index write; two parallel arrays `(type, hp)`. A generic `UndoDictRestore<K,V>` would just wrap a per-caller lambda — per CLAUDE.md's consolidation rule this is structural variance, a framework not a utility.
- Zone undos (`UndoZoneEdit/Place/Remove`) carry unique side effects (find-by-Id because indices shift, cancel in-flight drag, fix `SelectedZoneIndex`). Unit undos operate on a plain `List<PlacedUnit>` with index semantics. Not mergeable without losing behavior.

Two concrete micro-cleanups worth doing opportunistically:
1. **`UndoObjectRemove` (line 397) is dead** — never instantiated anywhere (both single-mode and paint-mode right-click removal push `UndoObjectBatchRemove`, lines 2422 and 2517). Delete it.
2. `UndoObjectPlace` (line 386, one call site at 2319) is exactly `UndoObjectBatchPlace` with a one-element list — optional merge, saves one class, zero behavior change.

---

## Finding 4 — Batch-place stroke finalization duplicated between Objects paint and ProcGen tab
**Verdict: CONSOLIDATE · Severity: low · Effort: S · Risk: low**

Evidence: the leftUp handler in the Objects paint path (`MapEditorWindow.cs:2352–2381`) and in `UpdateProcGenTab` (`5699–5727`) are near-verbatim copies: build `UndoObjectBatchPlace` from `_batchPlacedObjects`, optionally wrap in `UndoComposite` with an `UndoGroundStroke` from `_autoGroundStrokeOld`, push, null both fields. A comment at 5708 even says "mirrors the Objects paint stroke". Same divergence shape as Finding 2 — a fix to stroke finalization (e.g. rebake policy, composite ordering) must be made twice.

**Merged API sketch:**
```csharp
private void FinalizeBatchPlaceStroke()
{ /* body of 2352–2381; both call sites reduce to: if (leftUp) FinalizeBatchPlaceStroke(); */ }
```
Call sites: 2 (Objects paint leftUp, ProcGen leftUp). Note `PaintProcGen` itself is **not** a duplicate of `PaintObjectsBatch` — density-accrual random-attempt placement vs deterministic hex scatter is structural variance; keep the brushes separate.

---

## Finding 5 — Cost-field rebuilds: three complementary stages, not three overlapping paths
**Verdict: KEEP_SEPARATE · Severity: low (labeler over-match)**

Evidence (`World/TileGrid.cs:123–168`, `World/EnvironmentSystem.cs:1205–1239`):
- `RebuildCostField` (TileGrid.cs:154) — terrain → base `_costField`, parallelized. **Not legacy and not dead**: called at startup, from `Simulation.cs:376/3937`, from the editor wall-paint mouse-up (`MapEditorWindow.cs:3096/3124`), and from 8 scenario files. `WallSystem.BakeWalls` then stamps walls on top of this base.
- `RebuildTieredCostFields` (TileGrid.cs:123) — copies the base field into the 3 per-size-tier arrays; called by `EnvironmentSystem.BakeCollisions` (EnvironmentSystem.cs:1208) before re-stamping env obstacles.
- `RebuildTieredCostFieldsRegion` (TileGrid.cs:136) — deliberate perf variant of the tiered copy restricted to an AABB, used only by `RebakeCollisionRegion` (EnvironmentSystem.cs:1222) so removing one object doesn't re-copy "16.8M tiles × 3 tiers" (doc comment); it self-falls-back to the full rebuild if tier arrays are unallocated (line 145).

Pipeline: terrain → `RebuildCostField` (base) → `BakeWalls` (walls into base) → `RebuildTieredCostFields[Region]` (base → tiers) → stamp env objects per tier. Each method is a distinct stage or a documented dirty-region optimization; there is no third "legacy" path and nothing to merge. The full/region pair follows the codebase's standard full+region pattern (`BakeCollisions` / `RebakeCollisionRegion`).

---

## Summary of actionable work (all S-effort, editor-only, no Net/ or renderer contact)
1. Delete dead `PaintObjects` (~60 lines) and dead `UndoObjectRemove` class.
2. Merge `EraseWalls` into `PaintWalls(wallType)` + extract `FinalizeWallStroke()`.
3. Extract `FinalizeBatchPlaceStroke()` shared by Objects-paint and ProcGen leftUp handlers.
4. Leave the undo class family and the cost-field trio as-is.
