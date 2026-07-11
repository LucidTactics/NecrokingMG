# Investigate: mild Human-faction combat edge in symmetric fights

## Context
While building the `balance_matrix` scenario (2026-07-11), mirror-match sanity
checks (same unit def on both sides, one side Faction.Undead / other
Faction.Human, identical stats, morale forced fearless on both) showed the
Human-faction side winning more than chance: ZombieBoar mirrors went 17-9 for
whichever side was Human across 26 trials with spawn side and spawn order
alternated (and the effect followed the faction when factions were inverted via
`NECRO_BALANCE_INVERT=1`). ZombieWolf mirrors showed no such edge (13-11).

## What was ruled out
- Morale/routing: no `[Morale] BROKE` lines; both sides fearless (Undead rule /
  morale 100 >= MindlessMoraleThreshold).
- Attack cadence + damage: combat.log totals near-symmetric (Undead 215 attacks
  / 1547 net dmg vs Human 205 / 1525) in the inverted boar run — yet Human won.
- Targeting: FindBestEnemyTarget is nearest-enemy, faction-symmetric.
- Trample: can't trigger vs same-size targets, so not the boar-mirror cause.
- Horde systems: units not enrolled; archetypes stripped to AttackClosest.

## Notes
- Sample is small (~26 trials); could partly be noise, but it leaned the same
  way across three separate runs.
- Boar-specific suspects not yet checked: tusk knockback/physics interactions,
  death/corpse handling differences per faction (`_pendingZombieRaises`?),
  anything keying on `Faction.Undead` in the death pipeline.
- Repro: `NECRO_BALANCE_UNITS=ZombieBoar NECRO_BALANCE_TRIALS=16` with
  `NECRO_BALANCE_INVERT=1` vs unset, `bin/Debug/Necroking.exe --scenario
  balance_matrix --headless`, read log/scenario.log per-trial lines.
- The balance_matrix scenario now alternates faction per trial, so its
  measurements are insulated from this either way. This matters for REAL
  gameplay only if it's a genuine engine asymmetry (player zombies vs humans).

## Done when
Either the root cause is found (and fixed or declared intended), or a larger
trial count shows it was noise.
