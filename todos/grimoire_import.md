# Grimoire + spell tooltip imports (GodMenu3.unity)

Import 4 panels from E:/Nightfall/NightfallRogueRelease/Assets/__UIAssets/GodMenu3.unity,
visuals only (wiring to the magic system comes after user inspects).
Follow .claude/skills/importing-unity-ui/SKILL.md rigorously.

## Targets (exact scene names) + dumps (done)
1. "Spell Selection All On Top" → log/bag_inspect/grimoire_tree.txt (21558 lines!)
   - The spell grimoire: tabs (will later filter), spells 2-wide × several tall,
     vertical scrolling. Likely many repeated spell-entry subtrees → use an
     idempotent generator with row/entry sub-widgets (model: gen_utd_panel.py,
     auto-size + scroll later).
2. "Spirit Form Tooltip 2" → spirittip_tree.txt (3212 lines)
3. "Shadow Bolt Tooltip" → shadowbolt_tree.txt (4620 lines)
4. "Summon Wolves Tooltip 4" → wolvestip_tree.txt (11852 lines)
   - Three different spell-tooltip LAYOUTS (different spell kinds).

## Status
- [x] Subtree dumps written
- [x] Unity capture menu items added (CaptureUnitTooltip2.cs):
      Tools > Capture Grimoire / Spirit Form Tooltip 2 / Shadow Bolt Tooltip /
      Summon Wolves Tooltip 4 → unity_grimoire / unity_spirittip /
      unity_shadowbolt / unity_wolvestip _dark.png in log/bag_inspect/
- [x] Captures received: grimoire 706x1080, spirittip 428x271,
      shadowbolt 428x344, wolvestip 428x573 (log/bag_inspect/unity_*_dark.png)
- [ ] Study dumps: per panel list sections, sprites (+sizes/meta), swappers,
      TMP materials/auto-size (CAPTURE is truth for auto-sized text)
- [ ] Bake textures (max-height for scrollable/auto-size parts; icons via
      bake_unity_icon; check alphaIsTransparency/texel-space outlines)
- [ ] Generators: tools/gen_grimoire.py (+ gen for each tooltip; prefixes
      GM_/SPT_/SBT_/SWT_ or similar; reuse shared element styles where the
      swapper values match existing ones)
- [ ] Render in UIBlankWindow-style scenario at fixed crops, measure vs
      captures (region means, bboxes, row scans), iterate
- [ ] Review images saved separately per panel for user inspection
- [ ] Publish; user inspects; wiring (tabs filter, scroll, magic system) later

## Key engine facts for this work
- Auto-size: widgets.json `layout: vertical` + `autoSizeHeight` (rows collapse
  via SetHidden); image layers baked at MAX height crop from top.
- Scroll: widget def has Scroll/ScrollContentH fields (existing system — check
  how InventoryUI/CraftingMenu use it before inventing anything for the
  grimoire's vertical scrolling).
- Overrides: SetText/SetImage/SetTextColor/SetHidden/SetChildWidget per
  instance; sub-instance ids "{inst}.{childIdx}.{...}".
- Harmonize bakes happen in LoadDefinitions (world-load); per-element cache
  keys el:/bg:/st:/fr:.

## Progress 2026-06-12 (first pass done)
- tools/unity2widget.py: dump->widget converter (rect math anchors/pivot/
  stretch/y-flip, texture auto-copy to assets/UI/Imported/, verbatim swapper
  harmonize, TMP ratios 1.45/1.25). Special cases: EmbossedLeatherBorderInner
  -> widget bg layers (nine-slice + inset -17); skips children wider than
  root*1.05 (rotated slider poles).
- Widgets: GrimoireWindow (173 ch), SpiritFormTip (35), ShadowBoltTip (53),
  SummonWolvesTip (117). Scenario UIGrimoire renders all 4 at 1700x1250;
  review_*_vs_unity.png comparisons in log/bag_inspect/.
- Grimoire: structurally very close (tiles/tabs/ribbon/title all placed).
  Remaining: title font size/color (ours red+large vs dark), tile name text
  size, minor row spacing.
- Tooltips: all sections placed but TEXT OVERSIZED (stale TMP autosize -> use
  captures for per-text calibration like the resource tooltip lesson).
  Next: calibration pass per panel measuring glyph bboxes vs captures.
