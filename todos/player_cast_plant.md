# Player Cast Plant — design proposal (2026-07-04)

**Problem:** casting while running plays the (stationary-authored) cast animation while
the player keeps moving at full speed — the cast-pose slide. User wants: casting
decelerates the player to a stop while turning to the cursor, without the deceleration
adding cast lag, and without input pileup (held movement must not cancel the cast, and
must resume cleanly after).

Companion docs: [anim_movement_interaction_audit.md](anim_movement_interaction_audit.md)
(Rules 1/2 + R1-R6 proposal this integrates with), `docs/locate-behavior/animation.md`
(player `_pendingCastAnim` pipeline section, commit 2278e55). **Design doc — no code
changed in this pass.**

---

## 1. Current mechanics (verified)

- **Three cast paths** ([Game1.Spells.cs:424](../Necroking/Game1.Spells.cs#L424)
  `DispatchSpellCast`):
  - *Channeled* (`CastAnim` Imbue/Raise): raw-snaps `FacingAngle` to cursor (no turn
    rate, Spells.cs:492 + re-snap every frame in Animation.cs:166), Start→Loop→Finish
    machine, **effect at loop end**.
  - *Deferred* (`CastingBuffID` set): `Spell1` anim; **effect fires at the clip's
    effect frame** (`JustHitEffectFrame`, [Game1.Animation.cs:752](../Necroking/Game1.Animation.cs#L752)),
    with a left-Spell1 safety net at :783 that fires the spell anyway.
  - *Immediate* (no casting buff): effect on press, **no animation at all**.
- **Aim** is the press-time cursor world position, frozen in `PendingCastAnim.Target`.
  Mana + cooldown are committed **at press**.
- **Movement**: WASD → `PreferredVel` in the `PlayerControlled` branch
  ([Simulation.cs:1017-1084](../Necroking/Game/Simulation.cs#L1017)); Newtonian accel
  model with `maxDecel` (default 25, ≈5× accel) integrated in sub-steps
  (:1953-2004); sprint ramp 0→1 over 3s (down over 1s), multiplier to 4-5×.
  **Nothing reads the casting state in movement** — that's the whole bug. (Contrast:
  a player *melee* swing hard-zeroes Velocity via the `PendingAttack` plant — an
  instant teleport-stop, no decel curve.)
- **Facing**: cursor-facing at walk gait, velocity-facing above jog (hysteresis,
  :2409-2444), both through `FacingUtil.TurnToward` at ~360°/s. So a *sprinting*
  caster doesn't even face the cursor during a Spell1 cast today.
- **Anim path**: the player is `Archetype == 0` → **legacy** anim chain, not the
  archetype `AnimResolver` two-channel path. Cast states are priority-3
  non-interruptible; locomotion requests park behind them until the clip ends.
- **Existing cast lock**: `_pendingCastAnim != null` blocks a second cast, melee
  clicks, and Space-jump. No GCD; no input buffer (a press during a cast is eaten).

## 2. The sequence — brake first, THEN wind up (user correction 2026-07-04)

> The first draft proposed starting the cast anim at press and braking concurrently.
> **User corrected this**: the animation — and therefore the whole spell process —
> must WAIT until some deceleration is achieved. The cast pose must never appear on a
> still-sliding character; that IS the artifact being fixed, and the concurrent
> version merely shortens it.

The cast sequence:

```
press ──► BRAKE PHASE ─────────────► CAST PHASE ──────────────► RELEASE
          boosted decel toward 0      cast anim starts           effect frame fires the
          boosted turn to aim         (Spell1 / channel Start);  spell; held movement
          LOCOMOTION anim keeps       spell fires at its         resumes (+ tail cancel)
          playing, feet matching      effect frame as today
          the slowing ground speed
          gate: speed ≤ threshold
```

- **Brake phase**: locomotion continues *visually* — the run/walk cycle slows with
  actual velocity (the gait-from-speed selection + playback scaling already do this),
  so the character reads as "stopping to cast," never as sliding in a cast pose. The
  turn-to-aim runs during this phase too, so the pivot overlaps the brake.
- **Anim-start gate** ("some decel achieved"): start the cast anim when speed drops
  below a threshold — NOT necessarily a full stop. Candidate: `max(walkThreshold,
  X% of speed-at-press)` so a walking cast starts near-instantly and only a sprint
  pays a real brake window. Exact rule = open question Q1.
- **Cast phase**: unchanged from today once entered — Spell1 fires at
  `JustHitEffectFrame`; channels run Start→Loop→Finish.

**Latency accounting** (the cost the user flagged; both of their mitigations used):
sprint tops out ~4-6 wu/s; at `maxDecel × CastBrakeMultiplier` (~2× → ≈50 wu/s²),
braking from full sprint down to a walk-speed gate takes **~0.08-0.15s**; from walk
speed ~1 frame; from standstill zero. A 180° pivot at 3× turn speed (≈1080°/s) takes
≤0.17s and overlaps the brake. So the added input→effect latency is **zero when
stationary or walking, roughly a tenth of a second from a dead sprint** — and it is
*visible, motivated* latency (the character is skidding to plant) rather than dead
input lag. If feel-testing says it's still too long, the knobs are the brake
multiplier and a lower gate, not a redesign.

## 3. Proposed design — the Cast Plant state

One explicit state: **CastPlant** — active from successful dispatch of any *animated*
cast (deferred Spell1 + channeled; NOT immediate no-anim spells, which have no pose to
mismatch) until its release point. It is a *movement-owning* state per the audit's
Rule 1 invariant: it suppresses locomotion animation AND takes movement authority.

### 3.1 Sim bridge + the deferred anim start
`_pendingCastAnim` is Game1 state; the movement gates live in Simulation. Add
`Simulation.SetNecromancerCasting(bool active, float aimAngle)` following the existing
`SetNecromancerInput`/`SetNecromancerFacing` bridge pattern (Simulation.cs:909/921).
Game1 sets it on dispatch success and clears it at the release point.

The anim-start pokes move from dispatch time to gate time: `DispatchSpellCast`
validates + creates `PendingCastAnim` as today but in a new `WaitingForPlant` phase;
each frame (in `UpdateAnimations`, next to `TickPendingCastAnim`) Game1 checks the
gate (necromancer speed ≤ threshold) and only then issues `RequestState(Spell1)` /
`ForceState(channelStart)` and flips the phase to the normal casting flow. Everything
downstream (effect frame, channel loop budget, safety net) is untouched.

### 3.2 Braking (UpdateAI + accel model — not a hard zero)
While CastPlant: the player branch sets `PreferredVel = 0` and the accel integration
uses `maxDecel * CastBrakeMultiplier` (new `Settings.Combat.CastBrakeMultiplier`,
default ~2.0). **Do not copy the melee plant's `Velocity = 0`** — the smooth
integrate-down through the existing sub-stepped decel is what makes it a skid instead
of a freeze-frame. (Follow-up candidate: give the melee plant the same brake later so
player swings stop smoothly too — today they teleport-stop.)

### 3.3 Turning (frozen aim, boosted, through TurnToward)
While CastPlant: facing target = **the frozen cast aim** (`PendingCastAnim.Target`
direction), not the live cursor — the body should face where the spell will go.
Bypass the walk/jog facing hysteresis (a caster always faces the aim), rotate via
`FacingUtil.TurnToward` at `TurnSpeed * CastTurnBoost` (default ~3×). This **replaces
the channel path's raw `FacingAngle` snaps** (Spells.cs:492, Animation.cs:166) — the
snap becomes a fast turn, same code path for both cast kinds, and the
Tick-before-UpdateAnimations ordering issue disappears because facing is owned by
`UpdateFacingAngles` again. Held beam/drain channels (`_channelingSlot`) instead track
the **live** cursor at the boosted rate while channeling.

### 3.4 Release + move-cancellable recovery (the anti-queue mechanism)
- **Release point**: Spell1 → the effect frame (the same `JustHitEffectFrame` edge
  that fires the spell); channels → when Finish begins (Raise: loop end).
- **At release, if movement input is held**: cancel the anim tail (force locomotion)
  and let the held WASD re-enter `PreferredVel` immediately — the player accelerates
  out the same frame the payoff pops. If no input is held, the tail plays out
  naturally. This is the industry "recovery cancels into movement" rule and it is the
  entire answer to input pileup: **movement input is continuous state, never a queued
  action** — holding W during a cast doesn't cancel it, doesn't queue anything, and
  resumes automatically. Without tail-cancel, ending the plant at the effect frame
  would re-create the slide during the tail frames — these two must ship together.
- **Sprint ramp**: freeze `_sprintRampValue` during CastPlant (don't decay). A 0.4s
  cast otherwise eats ~40% of a 3s ramp — cast-weaving while kiting would feel
  punishing. Tunable; decay-at-half-rate is the fallback if freeze feels exploity.

### 3.5 Input semantics during the plant
| Input | Behavior |
|---|---|
| Movement keys | Never cancel the cast once the cast anim has started; ignored by movement while planted; resume (and tail-cancel) at release. During the *brake phase* (before the anim starts): open question Q2 — committed vs cancel-by-move. |
| Same/other spell key | Today: eaten. Phase B: **one-slot buffer** — remember the last spell pressed in the final ~0.25s (with *its* press-time cursor pos), dispatch at release if mana/cooldown still valid. One slot, latest-wins, no queue. |
| Melee click / Space jump | Stay blocked (existing `_pendingCastAnim` / `_pendingSpell` gates). |
| Knockback/knockdown/death | Cancel the cast (see 3.6). |

### 3.6 Hard-interrupt edge (pre-existing bug, becomes load-bearing)
Getting knocked down mid-Spell1 currently trips the left-Spell1 safety net
(Animation.cs:783) — **the spell fires anyway from wherever the player is**. With
CastPlant this would also strand the plant flag. Fix as part of Phase A: `Incap` /
`InPhysics` on the necromancer while `_pendingCastAnim != null` → cancel the pending
cast, clear CastPlant, **refund mana and reset the spell's cooldown** (both were
committed at press; refund needs new code). Interrupted casts that punish full cost
feel terrible; the interrupters are rare enough that refund is safe.

### 3.7 Data knobs
Global defaults in `Settings.Combat` (`CastBrakeMultiplier`, `CastTurnBoost`); a
per-spell escape hatch on `SpellDef` only if a real need appears (e.g. a future
`castOnTheMove: true` quick-spell that skips the plant entirely, or a heavy ritual
with a stronger brake). Don't pre-build the per-spell fields — the anim's own windup
length already scales plant weight with spell heft, which fits the north star
(anticipation proportional to payoff).

## 4. Integration with the audit's R1-R6 system

- **CastPlant is Rule 1 made concrete for the player.** The audit's invariant — *no
  state may suppress locomotion animation without owning movement* — gains a second
  legal form of "owning": besides zeroing (NPC plants), a state may **brake-to-stop**.
  R1's resolver gate should recognize both: an override is acceptable while moving if
  its state claims movement authority and applies a plant/brake. Cast, by design,
  *is* a movement interrupter — it goes on Rule 1's legal-interrupter list.
- **Phasing decouples cleanly.** The player is on the legacy anim path, so R1's
  `SetOverride` gate never sees him — CastPlant can ship before, after, or without R1
  (separate phase, as the user suggested). When the player eventually migrates to the
  archetype path (ai-architecture-review R8), the cast becomes an `OverrideAnim`
  request that passes R1's gate *because* CastPlant plants him — no redesign.
- **Shared vocabulary with the movement review**: CastPlant is one value of the
  movement-authority enum proposed there (P4-3: Normal|Physics|Charge|Dodge|Jump|…);
  if/when that enum lands, CastPlant folds in as `CastBrake` instead of a bespoke flag.
- **NPC unification (later)**: the same brake could replace the NPC hard plants
  (`InCombat`/`PendingAttack` `Velocity = 0`) so wolves/minions also *skid* into their
  attacks instead of freeze-stopping. Purely cosmetic upgrade, zero urgency.

## 5. Phasing

- **Phase A — core plant** (one PR-sized change): `SetNecromancerCasting` bridge;
  brake in the player UpdateAI/accel path; frozen-aim boosted turn replacing the
  channel raw-snaps; release at effect frame / Finish with move-cancellable tail;
  sprint-ramp freeze; hard-interrupt cancel + refund (3.6). Tuning constants in
  Settings.Combat.
- **Phase B — input polish**: one-slot cast buffer (~0.25s window); tune
  brake/turn/ramp numbers by feel.
- **Phase C — unification**: when R1 / the player-archetype migration / the
  movement-authority enum land, fold CastPlant into them (§4).

**Verification** (drive-game): sprint full speed, cast at a point *behind* the run
direction → expect: skid to a stop while pivoting ~180°, spell fires at the effect
frame from a planted, correctly-facing pose, and with W held the player is running
again the frame after the payoff. Also: cast while stationary (no visible change),
channel raise while holding W (planted for the full channel, resumes after Finish),
knockdown mid-cast (cast cancelled, mana refunded). A `--scenario` can assert
"necromancer velocity < walk threshold at spell-effect frame" once the feel is signed
off.

## 6. Decisions (user, 2026-07-04)

1. **Q1 — Anim-start gate**: (a) speed ≤ walk threshold. May adjust later.
2. **Q2 — Brake-window cancel**: (a) NO cancel — cast committed at press;
   mana/cooldown stay committed at press as today.
3. **Q3 — Tail-cancel**: (a) yes, recovery cancels into movement when input held —
   **but as a toggle in a new "Animation" settings tab** in the esc/settings menu
   (user also wants other sensible animation-feel settings gathered there).
4. **Q4 — Hard interrupt**: (a) cancel the cast AND refund mana + reset cooldown.
5. **Q5 — Sprint ramp**: (b) half-decay during the cast.
6. **Q6 — Immediate (no-anim) spells**: (a) castable on the move, zero delay.
