"""Audit each unit's CombatSpeed against the pixel-calibrated walk-anim feet-lock
velocity. Outputs a table:

   unit | combatSpeed | spriteWH | spriteScale | quadruped | walkStridePx | bodySubPx | pixelWalkVel | @MS plays | proposed CS

The "@MS plays" column is what playback rate the walk anim would actually use at
the current CombatSpeed. 1.0 = artist cadence (grounded + natural-looking).
Higher = sped up (grounded but rushed). Lower = slowed (grounded but lazy).

PropCS is computed against a TARGET cycle time (per-unit, size-based) rather
than the artist's authored cycle. So PropCS = "what CombatSpeed gives the
unit a target-cycle-time walk while keeping feet locked." User-supplied target
cycle times for the named creatures (human 1.2, wolf 0.8, deer 1.0, bear 1.7,
boar 1.0, greatboar 1.4); other units extrapolated by category + size.
"""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
UNITS_JSON = ROOT / "data" / "units.json"
CACHE_DIR = ROOT / "cache" / "stride"

# Target walk-cycle time (seconds) per unit. Size-scaled — bigger creatures
# get longer cycles (slower leg motion communicates bulk). Values for humans,
# wolves, deer, bears, boars, greatboars supplied by user; juveniles get
# shorter, dire/grizzly variants get longer. Zombies inherit from their
# living counterpart. Fallback for any unit not listed = the cached authored
# cycle (no override).
TARGET_CYCLES = {
    # Bipeds (human-class, ~1.8 wu tall)
    "skeleton": 1.2, "militia": 1.2, "knight": 1.2, "soldier": 1.2, "archer": 1.2,
    "pale_acolyte": 1.2, "wretched": 1.0, "necromancer": 1.2, "wight": 1.2,
    "lich": 1.2, "grand_necromancer": 1.2,
    "abomination": 1.5,  # bigger humanoid

    # Wolves
    "Wolf": 0.8, "ZombieWolf": 0.8,
    "DireWolf": 1.0,
    "JuvWolf": 0.6, "WolfCubZombie": 0.5,

    # Deer
    "FemaleDeer": 1.0, "MaleDeer": 1.0,
    "ZombieFemaleDeer": 1.0, "ZombieMaleDeer": 1.0,

    # Bears
    "Bear": 1.7, "GrizzlyBear": 1.8, "ZombieGrizzlyBear": 1.8,
    "ZombieJuvenileBear": 1.4,

    # Boars
    "Boar": 1.0, "ZombieBoar": 1.0,
    "GreatBoar": 1.4, "ZombieGreatBoar": 1.4,
}

def load_stride_caches():
    caches = {}
    for f in sorted(CACHE_DIR.glob("*.stride.json")):
        try:
            data = json.loads(f.read_text(encoding="utf-8"))
            atlas = f.name.replace(".stride.json", "")
            caches[atlas] = data.get("Units", {})
        except Exception as e:
            print(f"WARN: failed to load {f.name}: {e}")
    return caches

def compute_pixel_walk_vel(unit_cal, sprite_world_height, sprite_scale, is_quadruped, duty_cycle):
    """Returns (authored_walkVel, idle_spread, duty, authored_cycle, cycle_dist_world).
    authored_walkVel = walkVel using the artist-drawn cycle time.
    cycle_dist_world = stride distance per cycle in world units (cycle-independent)."""
    walk = unit_cal.get("Walk", {})
    stride_px = walk.get("StridePx", 0)
    cycle_sec = walk.get("CycleSeconds", 0)
    avg_h = walk.get("AvgPixelHeight", 0)
    idle_spread = unit_cal.get("IdleFootSpreadPx", 0) if is_quadruped else 0
    d = duty_cycle if duty_cycle and duty_cycle > 0 else 0.5
    if stride_px <= 0 or cycle_sec <= 0 or avg_h <= 0 \
       or sprite_world_height <= 0 or sprite_scale <= 0:
        return 0.0, idle_spread, d, cycle_sec, 0.0
    eff_stride = max(stride_px - idle_spread, 1.0)
    eff_world_h = sprite_world_height * sprite_scale
    px_per_world = avg_h / eff_world_h
    cycle_dist_world = (eff_stride / d) / px_per_world
    return cycle_dist_world / cycle_sec, idle_spread, d, cycle_sec, cycle_dist_world

def main():
    caches = load_stride_caches()
    units_data = json.loads(UNITS_JSON.read_text(encoding="utf-8"))

    rows = []
    skipped_dupes = set()
    for u in units_data.get("units", []):
        uid = u.get("id", "")
        # Skip the militia_copy* and skeleton_copy* duplicates — same sprite,
        # noise in the table.
        if "_copy" in uid:
            skipped_dupes.add(uid)
            continue
        sprite = u.get("sprite") or {}
        atlas = sprite.get("atlas", "")
        sprite_name = sprite.get("name", "")
        cal_unit = caches.get(atlas, {}).get(sprite_name)
        if not cal_unit:
            continue  # no calibration → skip (e.g. no Walk anim)
        cs = (u.get("stats") or {}).get("combatSpeed", 0)
        swh = u.get("spriteWorldHeight", 1.8)
        ss = u.get("spriteScale", 1.0)
        quad = bool(u.get("isQuadruped", False))
        duty = u.get("dutyCycle", 0)
        vel, idle, eff_duty, authored_cycle, cycle_dist = compute_pixel_walk_vel(
            cal_unit, swh, ss, quad, duty)
        playback_at_cs = (cs / vel) if vel > 0 else 0
        walk_stride = cal_unit.get("Walk", {}).get("StridePx", 0)
        # Target cycle (user-supplied for named units, fallback to authored).
        target_cycle = TARGET_CYCLES.get(uid) or TARGET_CYCLES.get(sprite_name) or authored_cycle
        # walkVel under target cycle = same stride per cycle, just spread over
        # a different time. PropCS sets CombatSpeed = this — locks feet AND
        # gives the unit the target cycle time at walk effort.
        prop_walk_vel = cycle_dist / target_cycle if target_cycle > 0 else 0
        rows.append({
            "unit": uid,
            "cs": cs,
            "swh": swh,
            "ss": ss,
            "quad": quad,
            "duty": eff_duty,
            "stride_px": walk_stride,
            "body_sub": idle if quad else 0,
            "authored_cycle": authored_cycle,
            "target_cycle": target_cycle,
            "vel": vel,
            "prop_vel": prop_walk_vel,
            "playback": playback_at_cs,
        })

    # Sort by @MS playback descending (worst skating first)
    rows.sort(key=lambda r: -r["playback"])

    # Print table — authored cycle (artist's drawn timing) vs target cycle
    # (user's size-based target) side by side, plus the PropCS that locks
    # feet AND hits target cycle when the unit walks.
    print(f"{'Unit':<22} {'CS':>5} {'Quad':>5} {'StrPx':>6} {'BodyS':>6} "
          f"{'AuthCyc':>7} {'TgtCyc':>7} {'walkVel':>8} {'@MS':>7} {'PropCS':>7}")
    print("-" * 96)
    for r in rows:
        proposed = round(r["prop_vel"], 2) if r["prop_vel"] > 0 else 0
        print(f"{r['unit']:<22} {r['cs']:>5.2f} "
              f"{'Y' if r['quad'] else '-':>5} "
              f"{r['stride_px']:>6.0f} {r['body_sub']:>6.0f} "
              f"{r['authored_cycle']:>7.2f} {r['target_cycle']:>7.2f} "
              f"{r['vel']:>8.2f} {r['playback']:>6.2f}x {proposed:>7.2f}")
    print(f"\n(skipped {len(skipped_dupes)} _copy duplicate units)")

if __name__ == "__main__":
    main()
