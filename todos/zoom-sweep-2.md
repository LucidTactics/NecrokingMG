# Zoom sweep round 2 — tracked checklist (2026-07-16)

Protocol: docs/vfx-zoom-audit.md. Constants-level audit by 3 parallel agents (all files
below read in full) + ladder verification. Status: **fixed+verified** / **fixed (ladder
pending)** / **verified** / **clean** / **flagged(user decision)** / **skipped(reason)**.

## Effect renderers (Necroking/Render/)
| Item | Status |
|---|---|
| BuffVisualSystem — auras/orbitals/flipbooks/weapon particles | clean; **lightning-aura arc widths FIXED (×Zoom/32)** — ladder pending (needs a shock-aura buff staged) |
| EffectManager draw path (GameRenderer.World `DrawEffectsFiltered`) | **Zoom² bug FIXED+VERIFIED** (impact puff measured 18px@32 → 75px@128 ≈ linear; was 16×) |
| ReanimEffectSystem / ReanimMorph | clean; outline consumer fix below covers its rise outline |
| PoisonCloudRenderer | clean (audit; r1 ladder ok) |
| DeathFogRenderer | clean |
| GroundFogSystem | **cull-margin FIXED** (world-based; was fixed 80px → wisps popped at edges zoomed in); sizes/drift clean; r2 ladder ok (mx shots) |
| WadingWakeSystem | clean (audit). Ladder skipped: empty_test has no water — stage on default map when convenient |
| MagicGlyphRenderer | clean |
| GrassTuftRenderer | clean |
| ShadowRenderer | clean |
| LungeSystem | clean |
| EnvGhostRenderer | clean (constant-px circle outline = documented placement-UI style) |
| LightningRenderer recheck | clean except **branch-cull 5px FIXED (×fxScale)** — bolt structure no longer changes with zoom; sky-bolt origin got its policy comment |
| GodRayRenderer recheck | clean except **noise x-phase FIXED (ray-relative, was raw screen X — pan/zoom banding drift)** |
| WeatherRenderer non-rain (fog/haze/flash/vignette) | clean (fog world-anchored; flash/vignette screen by nature) |
| Bloom | clean (r1 architecture) |
| FogOfWarSystem | clean (verified: feather is world-anchored via RT scale, not px) |

## GameRenderer partials
| Item | Status |
|---|---|
| Projectiles: arrow shaft/head | **FIXED** (thickness+head were constant px; length already scaled) — visible in any archer fight, ladder pending |
| Soul orbs | **FIXED** (6px/2px radii → ×Zoom/32) |
| Rope | **FIXED** (2px thickness → ×Zoom/32, 1px floor) |
| Walls dark top edge | **FIXED** (2px → ×Zoom/32) |
| Sprite outlines (ghost/reanim/buff-pulse/aim highlight) — shared `DrawSpriteOutline` | **FIXED** (offset ×Zoom/32, 0.6px floor) — one consumer covers all four |
| Carry offsets (body bag hilt nudge, corpse hand offset) | **FIXED** (px-at-32 → scaled at all 3 use sites) |
| Bagging + build progress bars | **FIXED** (match health-bar policy: ×Zoom/32, 1px floor) |
| Foragable pickup arc | **FIXED** (zoom was frozen at pickup time → live per-frame) |
| TableCraftMenuUI borders | **FIXED** (Scaled(t), 1px floor) |
| Units/env/corpses sprite draw, occlusion, culling | clean |
| Roads/ground tiles/ground shader | clean |
| Damage numbers / health bars / hover markers / aim circle / status glyphs | r1 decided (see policy flags) |

## Shaders
| Item | Status |
|---|---|
| GroundShader.fx | clean (all noise/scroll world-anchored) |
| WeatherFog.fx | fog clean; haze = screen-Y ramp, **flagged: deliberate screen-anchor, needs justification comment** (or user may want world-anchored haze) |

## Glue / spawn params — anchor-convention trio: FIXED (user-approved 2026-07-16)
- Zap end height: mid-sprite via /YRatio + SpriteScale + Z (was knee) — **fixed+verified** (paused zap at 64: hits chest).
- Projectile launch height /YRatio + volley follow-ups get caster Z — **fixed** (same conversion as the zap, code-verified).
- Sacrifice text lift /YRatio — **fixed**.
- Spirit Walk | clean (world-unit speed, generic ghost visuals; its outline fixed via DrawSpriteOutline).

## Policy decisions (2026-07-16)
- Rain soft curve sqrt(zoom/48) — KEPT (user-approved deviation; POLICY FLAG comment at the code site).
- Damage-number soft curve sqrt(zoom/32) — KEPT (user-approved deviation; POLICY FLAG comment at the code site).
- WeatherFog haze — **world-anchored** (fixed): ramp spans 45 world units north of camera
  (= zoom-32/720p view height, authored look preserved at tuning zoom) — verified at 8/48.

## Ladder debt (staging heavier than the fix; do opportunistically)
- Lightning-aura buff arcs at 8/32/128 (needs a shock-aura buff def staged).
- Wading wakes (water map). Arrow projectile (archer fight). Table-craft panel borders at 128.
