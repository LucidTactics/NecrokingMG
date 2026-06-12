# Unit Tooltip Panel — Unity → MonoGame UI recreation

Goal: recreate the Unity "Unit Tooltip2" panel (Red Dragon unit tooltip) from the
NightfallRogue project using our widget/UI system. Base window DONE + approved.
UnitDescription DONE + approved ("99%"). StatBoxMain DONE (pending review).
Remaining: EquipmentBox + AttacksBox + AbilitiesAndBuffs in one go (user trusts
after stats review). PROCESS: follow .claude/skills/importing-unity-ui/SKILL.md.

## StatBoxMain notes (2026-06-12)
- Generator: tools/gen_statbox_json.py (idempotent, regenerates all ST_*/st_*
  entries — edit it, not the JSON, for stats changes).
- Values use ROBOTO (assets/fonts/Roboto.ttf copied from FontSources/static):
  TMP 18 -> FS 25, bold(1.0) + underlay 1 black140 = 216 bright px (exact match).
- Labels: Quintessential TMP 15.65 -> FS 24 (desc style); auto-shrunk labels
  get child textOverride (Magic Power 22, Encumberance 21).
- Stats title: TMP 40 x 0.65 scale -> FS 34 (NOT 41 — measure, don't trust the
  ratio table), align center valign bottom.
- Zebra row swatches only on rows 1/3/5 (2/4 INACTIVE in Unity).
- Column separators: goldbar.png is ROTATED 90° in Unity (dump's rotation print
  is unreliable — check m_LocalRotation in the scene when geometry looks odd).
  Baked as goldbar_v_3x154.png.
- 'Protectrion' label typo is faithful to Unity's data (flag to user).
- Icons all existed locally at 24px 1:1; effective icon outline opacity =
  OutlineOpacity x OutlineColor.a (132/255).

## Workflow that works (per-section)
1. Ground truth: user runs Tools > Capture Unit Tooltip2 PNG in Unity (contents
   enabled) -> log/bag_inspect/unity_tooltip2_dark.png at native 468x746.
2. Spec: `python tools/dump_unity_subtree.py <scene> "Unit Tooltip2" log/bag_inspect/tooltip2_tree.txt`
   — full subtree with components, colors, harmonize values, resolved GUID asset
   paths (cache: log/bag_inspect/unity_guid_cache.json). Section line ranges:
   UnitDescription 249-861, StatBoxMain 862-4254, EquipmentBox 4255-8890,
   AttacksBox 8891-11206, AbilitiesAndBuffs 11207+.
3. Coordinate math: children of tooltip root use top-left-anchored rects;
   element rect = (pos.x - pivot*w, -pos.y - pivot*h) relative to parent's
   top-left + parent offset. WATCH FOR localScale (UnitPortrait subtree is 0.6;
   GodText 1.6667 — net scale multiplies sizes AND positions from anchor).
4. Author widgets/elements JSON, run UIBlankWindow scenario at 1280x900, crop
   (406,77,468x745), side-by-side + pixel-measure vs capture, iterate.
5. Font calibration: TMP fontSize != FontStash size. Measured: TMP title 55*0.65
   scale -> FontStash 67; TMP desc 13.5 (net scale 1.0) -> FontStash 21.
   Ratio ~1.2-1.55, calibrate per text by measuring glyph bbox in both renders.

## Source of truth (Unity project)
- Scene: `E:\Nightfall\NightfallRogueRelease\Assets\__UIAssets\CityMenu3.unity` (27 MB — grep it, never read whole)
- Object: `Unit Tooltip2` (GameObject fileID 1434266067), root size 467.6 x 745.5
- Color harmonizer script: `Assets\Z_Lib\Scripts\MaterialColorSwapperHandler.cs`
  (HSV blend toward TargetColor by PercentNewHue/Sat/Value — maps 1:1 to our
  HarmonizeSettings hue/sat/valStrength)

## Done (2026-06-11)
- `UnitTooltipWindow` widget in `assets/UI/definitions/widgets.json`:
  bg `LeatherBackground` (EmbossedLeatherBorderInner.png) scale 0.14, inset -17,
  harmonize target (31,24,17) hue 1 / sat .756 / val .588, gradColor (18,15,11,112)
  gradStrength 0.992; frame `RenaiThinBorder16` scale 0.25, inset 0, harmonize
  target (111,92,57) hue 1 / sat .75 / val .665. All values copied from Unity
  MaterialColorSwapperHandler and VERIFIED against an isolated Unity capture:
  interior bands match within ±3/255, frame is 4px gold + 1px dark edge on both.
- Unity ground-truth capture: `Assets/Editor/CaptureUnitTooltip2.cs` in the Unity
  project (Tools > Capture Unit Tooltip2 PNG) renders the panel isolated to
  `log/bag_inspect/unity_tooltip2_dark.png` (+_alpha) at native 468x746 on the
  same (58,62,72) backdrop as the test scenario. Re-run it after Unity-side edits.
- New widget props `frameScale` + `frameInset` (and negative insets allowed):
  UIEditorWidgetDef, both JSON loaders, editor writer, both draw paths, editor
  inspector fields. Negative bg inset = overhang (EmbossedLeatherBorderInner has
  a 122px baked-in transparent margin; Unity oversized the rect the same way).
- Vertical gradient support in HarmonizeSettings (`gradColor` RGBA + `gradStrength`):
  baked into the harmonized texture copy in ColorHarmonizer.HarmonizeTexture as
  lerp(pixel, gradColor.rgb, yFrac * gradStrength * gradColor.a/255), top->bottom.
  Matches Unity's VerGradD2U shader behavior. Round-trips through all three JSON
  paths (HarmonizeSettings.Read/Write, RuntimeWidgetRenderer, UIEditorWindow).
- `RenaiThinBorder16` nine-slice (border 16 = Unity's slicing). The old
  `RenaiThinBorder` (border 10) under-slices the ~16px line art so leftovers smear
  from the nine-slice center region — kept as-is since other widgets use it.
- `UIBlankWindow` scenario + `WantsWidgetRenderer`/`WidgetRenderer` plumbing in
  ScenarioBase/Game1. Run:
  `bin/Debug/Necroking.exe --scenario UIBlankWindow --timeout 30 --headless --speed 10 --resolution 1280x900`
  (headless default backbuffer is 320x240 — always pass --resolution for UI shots)
- `tools/crop_image.py` — crop+magnify screenshots for pixel inspection.

## Done (2026-06-11, session 2): UnitDescription section
- New system capabilities added for this:
  - Element-level `harmonize` now works at RUNTIME (was editor-only): loaded in
    RuntimeWidgetRenderer.ReadElements, generated in GenerateHarmonizedTextures,
    drawn via per-element cache prefix "el:{elementId}" (NOT plain "el" — two
    elements can harmonize the same texture differently; editor updated to match).
  - Word wrap implemented in BOTH render paths via shared
    WidgetLayoutUtils.WrapText (was serialized but never rendered). Set via child
    textOverride { wordWrap: true } — NOTE: textOverride fontSize defaults to 14
    and align/valign to left/top when omitted, so always specify the FULL style
    in an override.
- New defs: nine-slices CircleBox (CircleFrame2Inner, borders 126/129/129/133),
  SwatchBanner (Swatch1.3, 10/7/9/8); widgets UD_Box, UD_PortraitFrame(Shadow),
  UD_TitleSwatch; elements UD_* (pattern, parchment, stencil, portrait, heraldry,
  description, title + title shadow x4 for fake outline).
- Assets: copied Quintessential.ttf -> assets/fonts (OFL license, from Unity
  project); dragonpattern-transparent.png + heraldry_trans.png -> assets/UI/
  Background; Parchment-2-Tile.psd converted to PNG -> assets/UI/Patterns.
  Baked Unity "Tiled" image modes into pre-tiled/cropped textures via PIL:
  dragonpattern_ud_box.png, heraldry_title_strip.png (our images only stretch).
- Verified vs capture: title glyph bbox within 3px on all edges, swatch band
  rows 5..42 exact, portrait stack correct, leather box + gradient match.

## Done (2026-06-11, session 3): review fixes — LESSONS THAT APPLY TO ALL SECTIONS
- **Premultiplied-alpha bug in HarmonizeTexture (fixed)**: textures load
  premultiplied; the harmonizer was color-shifting premul RGB, brightening
  semi-transparent edge pixels into white halos and mangling faint patterns.
  Now un-premultiplies -> shifts/gradient/outline -> re-premultiplies.
- **Outline feature added to HarmonizeSettings**: outlineColor/outlineThickness
  (texture px)/outlineOpacity — baked silhouette outline mirroring the Unity
  shader's outline params. Serialized in all 3 JSON paths; editor now has
  outline + gradient controls in the shared DrawHarmonizeSliders panel
  (EditorBase — env-object editor gets them too).
- **PointClamp + minification = splotchy mess**: our UI batch point-samples, so
  ANY texture drawn below its source size aliases badly (Unity samples
  bilinear). RULE: pre-bake every Unity "Tiled"/minified/magnified image at its
  EXACT display size (premultiplied-aware LANCZOS — see resize_premul in this
  session's bake scripts). Baked: dragonpattern_ud_box 452x157, heraldry strip
  457x40, dragonpattern_stencil 140x143, Dragon3-Transp-Bust2_138 138x139,
  Swatch1.3_457x40 (swatch switched from nine-slice widget to image element —
  its 3px slicing wasn't load-bearing).
- **Title font calibration corrected**: 67 was wrong — the Unity glyph bbox I
  measured included the drop shadow. Strict face-color filter says 56. When
  calibrating text, filter tightly to glyph face color and ignore shadows.
- Per-element alpha/tint/harmonize values must each be read off the dump
  (BoxTexture tint 178,178,178,50 ≠ BoxStencil 255,255,255,25 etc).

## Done (2026-06-11, session 4): banner nine-slice root cause + line spacing
- **Unity sliced sprites: small borders are load-bearing.** The swatch's tiny
  9-slice borders (10/8/7/9 px) compress the texture's feathered/transparent
  END regions into ~3px caps, so the opaque body fills nearly the whole rect.
  A uniform whole-texture resize spreads those margins proportionally — banner
  rendered inset/short. RULE: bake Unity sliced sprites with
  `tools/bake_nineslice_image.py <src> <w> <h> <srcL,B,R,T> <dstL,B,R,T> <out>`
  (dst border px = src border / (spritePPU * pixelsPerUnitMultiplier / 100)).
  After fix: banner blue spans x7..460 vs Unity 7..461.
- **lineSpacing text property added** (textRegion + textOverride, both loaders,
  both writers, both render paths via shared WidgetLayoutUtils.DrawTextBlock —
  which also does per-line alignment and integer-pixel positioning). Line
  advance derived from MeasureString two-line minus one-line (version-proof).
- Description calibrated: font 21, lineSpacing 2, box 250 wide at x173 — wrap
  points now match Unity's lines 1-3,5-6 exactly (line 4 differs by one word;
  font metric variance). Pitch matches Unity's ~15px.
- Title outline restructured: thin ring (4 copies at ±1) + drop shadow copy at
  (+3,+3), matching TMP's outline+shadow look better than the old thick ±2 ring.

## Done (2026-06-11, session 5): TMP material inspection + text features
- **Always read the TMP MATERIAL, not just m_fontColor.** Effective glyph color
  = m_fontColor x material _FaceColor (DropShadow.mat: warm HDR 1.40/1.27/1.04),
  plus SDF coverage at small sizes blends toward the background. Calibrate by
  matching MEAN glyph-face color between captures, not the serialized color.
  Title (237,207,167), description (242,212,170) — means now within ~5/255.
- **TMP underlay = tight shadow**: _UnderlayOffset (0.3,-0.5) + dilate/softness
  hugs the glyph. Fake with outline ring at +-1 plus one copy at (+2,+2); a
  detached +3,+3 copy reads as a gap.
- **TMP fontStyle Bold widens advances ~15%**: added `charSpacing` (float px)
  and `bold` (double-draw at +1px) to textRegion/textOverride — both loaders,
  writers, WrapText, DrawTextBlock. Title: charSpacing 2 + bold -> width 187 vs
  Unity 192 (was 168). Description: bold only (wrap unchanged).
- **Unity window frame is asymmetrically inset** (~2px narrower on the right —
  Window/Border offsetMin/Max in the scene). Added `frameInsetR` widget knob
  (UnitTooltipWindow: 2). Banner -> dark edge -> frame now matches.
- Title center calibrated to Unity's 228 (3px left of rect center — TMP margin
  asymmetry m_margin (-16.9,0,-52.9,0)).

## Done (2026-06-12, session 6): real text outline + title baseline
- **Crisp text outline via FontStashSharp Stroked effect** (per-call API in
  1.5.4: `batch.DrawString(..., effect: FontSystemEffect.Stroked, effectAmount: w)`
  — NOT FontSystemSettings, that API form doesn't exist in this version).
  New textRegion/textOverride props: `outlineWidth` (px) + `outlineColor` RGBA.
  Replaces the 4-offset-copy ring hack — much crisper. Title: width 2 dark
  (25,14,8); description: width 1 (20,12,8,200) ~ TMP underlay; one offset
  copy kept for the title's drop shadow only.
- **Title baseline fixed**: DrawTextBlock's vertical centering uses
  MeasureString('Ay') block height which differs from the old whole-string
  measure — when switching text rendering code, ALWAYS re-verify glyph row
  positions against the capture. Baseline now 37 vs Unity 35 (banner bottom 42).
- Description: outline replaces bold (outline already adds the weight; both
  together read too heavy). Title keeps bold + charSpacing 2 + outline 2.
- Heraldry overlay tint alpha 18 -> 12 (banner pattern std now 2.43 vs 2.29).

## Done (2026-06-12, session 7): description weight root cause
- **Outline visually eats the glyph face** even though FontStash's Stroked pass
  is geometrically outward: the glyph's antialiased edge pixels composite over
  the dark stroke instead of the light background, so the bright face shrinks
  by ~the AA rim (measured: 2188 face px no-outline -> 1778 with outline).
- **TMP Bold = face dilation**: DropShadow.mat has _WeightBold 0.75 and the
  Unity texts use fontStyle Bold — the face is fattened BEFORE the underlay.
  Our equivalent: `bold` + new `boldStrength` (0..1, opacity of the +1px bold
  pass — sub-pixel weight analog). Calibrated by matching bright-face pixel
  counts: desc bold 0.7 + outline 1 -> 2446 face px vs Unity 2447.
- METHOD for text weight: count bright face pixels in the same region of both
  captures, tune boldStrength until they match. Use for stats/equipment text.

## Done (2026-06-12, session 8): TMP underlay model implemented correctly
- **Text dark-edge = UNDERLAY (behind), not outline (in front/around)**: TMP
  underlay is a dilated, offset, softened silhouette behind the face. Our
  implementation: the outlineWidth/outlineColor pass is a SINGLE stroked draw
  tinted with the (dark) underlay color, drawn before the face, with
  outlineOffsetX/Y for the bias. Description: width 1, black @190, offsetY 1
  (Unity: dilate .44 + softness .3, offset .3/-.5 at 13.5px -> scaled ~1px/0,1).
- **FontStash Stroked GOTCHA**: the stroked glyph bitmap bakes the stroke BLACK
  and the face white — the tint colors the face only; the stroke is always
  black. A "face-colored dilation pass" is therefore actually a black ring
  (face px collapsed 2188 -> 352 when tried). Dark-tinted stroked draw = dark
  dilated silhouette (what an underlay needs).
- **Never double the dark pass**: bold applies a +1px pass to the FACE only;
  doubling the underlay/outline pass at partial alpha = the "muddy" artifact.
- **effectAmount is per-direction px dilation**: amount 2 at 21px body text
  floods inter-letter gaps. Keep 1 at body sizes.
- **TMP underlay values are NORMALIZED SDF-space ratios, not pixels** (user
  insight: inspector clamps offset to [-1,1], dilate/softness to [0,1]). The
  (0.3,-0.5) offset is SUB-pixel — the dilated rim stays visible on ALL sides
  of each glyph. Our integer offset of (0,1) killed the top rim; correct
  translation is offset (0,0) + dilation 1. Underlay alpha lowered 190->150 to
  emulate the softness-faded rim (softness 0.3 lightens their effective edge).
- Final desc settings: bold 0.7, underlay width 1 black @150, no offset.
  Face px 2499 vs Unity 2447 (~2%).
- Remaining nuance: TMP softness (soft falloff) not reproduced — our dilation
  edge is crisp but alpha-compensated. Could try a Blurry-effect underlay pass
  if it ever matters.

## Done (2026-06-12, overnight): FULL PANEL + two new panels
- Unit Tooltip2 COMPLETE: StatBoxMain + EquipmentBox + AttacksBox +
  AbilitiesAndBuffs (generators: tools/gen_statbox_json.py, gen_equipbox_json.py,
  gen_attacksbox_json.py, gen_abilities_json.py). Flag/X_CloseButton INACTIVE
  in Unity -> skipped. Review: log/bag_inspect/review_unit_tooltip_vs_unity.png.
- NEW PANELS (user request, built from scene data + reference screenshots since
  no Unity capture was available overnight — geometry of INACTIVE Unity nodes
  is STALE, fitted to reference images):
  - CommanderEquipWindow (312x366, gen_extra_panels.py): Templar banner, 7
    equipment slots (CircleFrame2Inner + ClothUpgradeThinFrame bakes, ghost
    icons, labels), SampleUnit + radial shadow, 3 bottom buttons.
    Review: log/bag_inspect/review_commander_box.png
  - StatTooltipWindow (222x103): leather window + dragonpattern stencil + gold
    inner-edge lines + strength icon + underline + title + wrapped desc.
    Review: log/bag_inspect/review_stat_tooltip.png
  - Brightness vals for commander tuned EMPIRICALLY vs reference (bg val
    0.48->0.95 etc) — REDO against real captures: Unity menu now has
    Tools > Capture CommanderTaskEquipmentBox (3) PNG and
    Tools > Capture Stat Tooltip PNG (outputs unity_commander_box_dark.png,
    unity_stat_tooltip_dark.png in log/bag_inspect).
- **BUG FIX: widget-layer harmonize cache collided across widgets** sharing a
  texture (bg|path). Now per-widget: "bg:{id}"/"st:{id}"/"fr:{id}" in runtime +
  editor (same class of bug as the earlier element "el:{id}" fix).
- Scenario draws all three: UnitTooltipWindow @(406,77 crop), CommanderEquipWindow
  @(60,80), StatTooltipWindow @(950,80) in UIBlankWindow.

## Known gaps vs Unity original (all minor, verified against capture)
- Text drop shadow / SDF outline: title fakes the outline with 4 offset dark
  copies; description has no drop shadow (TMP material effect). Subtle.
- Description wraps to 8 lines vs Unity's 9 (different font metrics); line
  spacing slightly different (FontStash leading vs TMP).
- Portrait white outline reads slightly softer than Unity's (coverage-faded
  bake vs shader); thickness 2.5 @ 0.329 on the 138px baked portrait.
- Harmonize val occasionally needs a small empirical nudge (Unity shader works
  in linear color space; our CPU harmonize in gamma): swatch val 0.46->0.44
  landed within 2/255 of the capture.
- Gradient top/bottom is ~1% off (our bake runs over texture Y incl. transparent
  margins vs Unity's rect UV) — imperceptible, accepted.
- Second border layer ("WindowBorder (1)") shows as an extra bright line at the
  very bottom edge of the Unity capture (row 742: 203,164,92) — we draw one frame.
  Stencil layer could host a second border if the doubled look matters.
- No editor UI yet for gradColor/gradStrength in the harmonize panel (JSON only;
  values round-trip safely through editor save).
- Text outline/shadow — not supported in widget text (Unity used TMP + shader outline).
- Title font: Unity used "Quintessential SDF" — we don't have Quintessential.ttf;
  closest existing families: Medieval / GrandStrategy (see assets/fonts/).

## Full panel spec (from Unity scene, for next phase)
Sections under Unit Tooltip2 (all ~452-463 wide, vertical stack):
- UnitDescription (452x186): UnitPortrait (harmonized, tint 255,235,235) + Title
  text "Red Dragon", font size 40, white, bold, centered.
- StatBoxMain (463x165): VerticalLayout of rows, each row 459x24
  HorizontalLayout spacing 13; row = semi-transparent swatch (255,255,255,61;
  harmonized dark 20,18,14) + 3 columns of icon+value. Stats: HP, Spirit, Morale /
  Size, Toughness, Magic Power / Strength, Protection, Coverage / Attack, Defense,
  Precision / Speed, Encumbrance, Upkeep.
- EquipmentBox (463x227): VerticalLayout spacing 3, rows of item icon + name +
  number columns. Icons in our assets/UI/Icons/Equipment + Stats.
- AttacksBox: same structure (Broadsword Slash / Stab / Dagger Stab rows).
- AbilitiesAndBuffs footer header.
- X_CloseButton top-right.
Our stat icons: assets/UI/Icons/Stats/ (HP_Icon, Spirit_Icon, Morale, Size_Icon,
Toughness_Icon, Magic_Power_Icon, Strength_Icon, Protection_Icon, Coverage,
Attack_Icon, Defense_Icon, Precision_Icon, Speed_Icon, Encumberance_Icon...).

## Debugging
- Scenario log: log/scenario.log (bin/Debug/log/ when run from repo root)
- Screenshots: bin/Debug/log/screenshots/ui_blank_window.png
- Crop: `python tools/crop_image.py <png> <x> <y> <w> <h> <scale> <out.png>`


## Session 2026-06-12 — Commander box review round + Resource HUD tooltip
All three side panels user-approved. Final state:
- CommanderEquipWindow: bg val 0.72 (tint-before-swap compensation; verbatim
  0.48 + cool tint 199,215,229), slot tex val 0.325, border val 0.302 + outline
  (55,25,9) 1.56/0.257, TabStencil thatch a22 + ButtonHighlight Cloth_66x64 a96
  added, per-slot icon alphas 91/99/80/113/99/128 + white outline 0.812,
  title fs 75 cs 2 (UD recipe), banner Swatch1.3_304x56 (transparent tail
  cropped) + RenaiThinBar 304x5 GPU-sampled + 1px seam line (25,18,12,160),
  blacksmith stencil true rect (25,79,287,246).
- ResourceTooltipWindow (222x231): reuses ST window recipe wholesale; title
  fs 24 cs 1.4, labels fs 24 cs 1.25 warm (248,219,184), values Roboto fs 22
  colors x0.95 (202,179,143 / 88,130,90 / 153,45,37), header 20 fs 30,
  desc fs 21 ls -4 w 200 (stale TMP autosize: serialized 12, real ~21).
- Outline model CORRECTED: thickness = SOURCE texels, scales with draw; bake
  at source res then downscale (bake_unity_icon.bake_outline, portrait op
  0.416->0.62 LANCZOS compensation). Falloff = clamp(th+1-dist) hard band,
  NOT th/dist (1/d tail doubled apparent width). Validate with intensity-vs-
  distance profiles, never run-length threshold counts.
- Unit shadow: DiffuseParticleSprite is RGB luminance ramp, alphaUsage 2
  (FromGrayScale) -> alpha from luminance in bake.
- Capture tool has menu items for all 4 panels (Unity: Assets/Editor/
  CaptureUnitTooltip2.cs).
