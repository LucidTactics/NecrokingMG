# VFX Zoom Audit — protocol, fixtures, and lessons

Born from the 2026-07 zoom-correctness campaign (~20 commits: rain, health bars,
damage numbers, beams, drains, bloom architecture, god ray). That campaign found
real bugs but ALSO produced a costly meta-lesson: the audit itself had misses
(whole systems skipped, defects inside "checked" systems), several fixes were
middle-ground compromises that had to be ripped out later, and verification was
reactive instead of systematic. This doc is the protocol that prevents a repeat.
Conventions live in [locate-behavior/camera.md](locate-behavior/camera.md); this
is the *procedure*.

## The model (decide once, derive everything)

**Realism is the guide** (project decision, 2026-07-16): every visual is a world
object. A light source illuminates a fixed *world* distance of air; a beam has a
*world* width; a puff has a *world* size. Zoom only changes how many pixels that
world occupies — never the world quantities themselves, and never the intensity.

1. **Classify every element** exactly once: **world-scaled** (default for ALL
   VFX), **screen-constant** (ONLY legibility UI: text, icons, gizmo handles —
   never effect geometry), or **screen-anchored** (needs a written justification
   in a comment; e.g. "the sky" for off-screen bolt origins).
2. **One effect = one policy.** Every dimension of a VFX (widths, arc heights,
   wave amplitudes, cloud sizes, drift speeds, anchor offsets, scroll speeds)
   scales the SAME way. The drain shipped with linear structure + sqrt-damped
   clouds "to look better" and the parts visibly detached across zoom.
3. **No middle-ground curves.** Every compromise tried during the campaign
   (sqrt damping, scale-floor clamps, per-octave intensity compensation,
   one-sided biases) later proved to be a wrong model wearing a tuning knob, and
   each cost a full report→diagnose→rip-out cycle. If the correct model seems to
   need a compromise, the *architecture* is wrong — fix that (the bloom needed
   virtual-resolution rendering, not blend weights). The only sanctioned
   deviation is a physical limit (e.g. blur-resolution floor), and its fallback
   must also be derived from the model (energy dimming), not invented.
4. **HDR perceptual trap:** with intensities ~10x over the tonemap knee, blend
   *weights* barely move a glow's visible edge — only *footprint* changes do.
   Any interpolation scheme built on weights will read as thresholds. (This
   single fact invalidated four successive bloom fixes.)

## The audit unit is every CONSTANT, not every system

The misses were not missing `* Zoom` on sizes — they were **offsets, anchors,
and directions**: the god ray's convergence point (a screen-space sky offset),
the drain wave's hardcoded 5px amplitude, damage numbers' world-unit rise under
a fixed text size. For each literal and data field in a VFX path, answer:

- **Unit**: px-at-zoom-32? world units? screen fraction? UV? (Data-authored px
  values are ALWAYS "at zoom 32" — see camera.md.)
- **Anchor**: world point? sprite rig (`WorldToScreenPx(pos, h*Zoom)`)? screen?
- **Motion**: does anything move (drift, scroll, fall)? Speed must be world
  units/sec on screen (px speed ∝ zoom). Flipbook FRAME RATES are exempt.
- **Relationships**: offsets *between* two drawn things (fan spread, cluster
  bias, convergence direction) are sizes too.

A `* fxScale` on the width while the anchor offset stays screen-space is how
"scales correctly" and "converges to a wandering point" coexist.

## Sweep protocol

1. **Enumerate first, into a tracked checklist file** (e.g. `todos/` during the
   sweep). Sources: every renderer under `Necroking/Render/` +
   `GameRenderer.*.cs` draw paths + every visual kind in `data/spells.json`
   (strike/zap/beam/drain/godray/projectile/impact/buff/targeting) + ambient
   systems (weather, ground fog, death fog, poison, reanim, wakes, shadows,
   glyphs). Every item ends as **verified** / **fixed+verified** / **skipped
   with written reason**. The god ray was lost to an untracked "skip, looks
   separate" made mid-implementation — silent skips are the #1 cause of misses.
2. **Stage each effect deliberately** (drive-game). Known recipes:
   - `spawn_lightning <zap|strike|beam|drain> <spellID> <x> <y> duration=999` —
     visual-only, any duration, no cast pipeline.
   - `hdrbar on [len] [width] [intensity] [count] [gap]` — the controlled HDR
     rectangle(s); `count/gap` for between-beam fill tests. `bloomdim <v>` tunes
     the zoom-out dim live.
   - Real casts: `set_mana necro 9999`; cast time ≈1.5s (casting buff), so wait
     ≥1.8s before pausing. Dev `damage` spawns no damage numbers — use
     `fireball x y dmg`. Lifedrain targets corpses only. Friendly skeletons walk
     to the necromancer — order them away with `move` or accept short beams.
   - `weather <field> <value>` for visible rain; `setting weather.enabled ...`.
3. **Pause the sim, then ladder the SAME frame**: `pause`, then camera shots at
   zoom **8 / 15 / 24 / 32 / 45 / 64 / 90 / 128** (fine steps across any band a
   report mentions — coarse ladders miss threshold bugs). Judge *proportions*
   (effect-to-unit, glow-to-core), not absolute sizes.
4. **Motion pass**: unpaused shot pairs ~0.2s apart at two zooms — on-screen
   drift/scroll/fall speeds must scale with zoom.
5. **Anomaly? Fixture first.** Reproduce on `hdrbar` (or build a new fixture —
   one dev command) before touching effect code. Isolating away
   flicker/jitter/style data is what cracked the bloom problem in one step
   after days of in-situ guessing.
6. **Instrument before iterating.** Put the relevant numbers ON SCREEN (the
   temporary zoom/bloom widget pattern — zoom, code-path values, top-right,
   `_smallFont`) so every user screenshot carries the data. "Thin at 64.2,
   thick at 57.8" with widget numbers pinpointed a discontinuity that four
   blind fixes had missed.
7. **Verify the actual effect, never a proxy.** The drain regression shipped
   because a zap "exercises the same path" — it didn't exercise the volumetrics.
   Every changed effect gets its own ladder before commit.
8. **When observation contradicts your math, stop theorizing and bisect the
   pipeline empirically** (toggle passes, pin inputs, screenshot between
   stages). That's how `InverseBlendFactor` corrupting destinations was found —
   see [known-platform-bugs.md](known-platform-bugs.md). Two identical-math
   frames rendering differently means a platform lie, not a subtle equation.

## New-VFX checklist (run for ANY new or changed visual effect)

1. Every constant classified (unit + anchor + motion), world-scaled by default;
   any screen-anchored term has a justifying comment.
2. One scaling policy for the whole effect — no per-part curves.
3. Pixel-authored data fields are px-at-zoom-32; scale by `Zoom/32` (`FxScale`
   pattern in LightningRenderer; editor previews pass 1).
4. Ladder-verified: paused screenshots at zoom 8 / 32 / 128 minimum, plus one
   motion pair if anything animates spatially.
5. If it's bright (HDR): check it over the bloom at far zoom (fat halo?) and
   near zoom (fill/overlap where multiple emitters sit close).

## Candidate improvements for future rounds

- A checked-in `--scenario` that stages one instance of each effect family and
  screenshots the ladder (regression harness — currently all verification is
  manual re-driving).
- A zoom slider in the spell-editor preview (SpellPreview is pinned to the
  authoring view, so authors never see other zooms at edit time — gaps ship).
- Re-add the on-screen zoom/bloom widget behind a dev command (`zoomhud`) at
  the start of any sweep; it was deleted after sign-off (commit `e0c07d3`
  removed it; trivial to restore from `0a5a970`).
