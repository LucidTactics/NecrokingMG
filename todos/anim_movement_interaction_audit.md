# Animation ↔ AI ↔ Engine Interaction Audit (2026-07-04)

**Trigger:** user saw a fleeing deer stuck in a dodge/block or idle frame while still
moving (sliding). This is a *review/audit document* — no code changed in this pass.
Companion to [anim_combat_review.md](anim_combat_review.md) (the earlier deep review;
its Round 1/2 fixes are all in) and `docs/locate-behavior/animation.md` (new map page
with the full writer census, committed 60b9773).

**The user's two design rules, which this audit measures against:**
- **Rule 1 (moving):** while a unit is moving, only things that can interrupt *movement*
  (knockback, knockdown, death) may interrupt the *movement animation*.
- **Rule 2 (stationary combat):** attack anims take priority over got-hit/avoid/block
  anims; block/dodge have cooldowns so a surrounded unit doesn't twitch; pacing leaves
  idle windows for anims to play out.

---

## 1. Root causes of the sliding deer (both confirmed in code)

### 1a. Melee-miss Dodge has no fleeing/moving gate
[Simulation.cs:3148-3158](../Necroking/Game/Simulation.cs#L3148) — on a missed melee
swing, the *defender* gets a `Dodge` Priority-1 OneShot override. Gates: `!Incap.Active
&& JumpPhase == 0` only. The on-hit flinch (`DamageSystem.ApplyHitReactAnim`,
[DamageSystem.cs:63-73](../Necroking/Game/DamageSystem.cs#L63)) learned the
Fleeing/Routing/refractory gates in the last round — **the miss-dodge site did not**.
A wolf chasing a fleeing deer whiffs repeatedly → Dodge re-stamped over the Run
locomotion while the deer keeps moving → dodge-pose slide. There is also **no dodge
cooldown** at this site (`DodgeTimer` only covers the trample dodge-hop), so repeated
whiffs re-trigger it back-to-back.

### 1b. Deer has no BlockReact clip → flinch renders as Idle; first hit races the flee flag
- `FemaleDeer` sprite clips (Animals.spritemeta): has Dodge/Attack1-3/BlockBreak/…, but
  **no `Block` and no `BlockReact`**.
- [AnimController.ResolveAnimForState](../Necroking/Render/AnimController.cs#L393):
  missing clip + no fallback-chain entry (BlockReact has none,
  [GetFallbackAnimName](../Necroking/Render/AnimController.cs#L443)) → **silent fallback
  to the Idle clip** while `CurrentState` stays BlockReact. The OneShot then runs the
  *full Idle clip length* (~0.85s — 28 frames), not the intended 0.35s flinch.
- The flinch's `Fleeing` gate reads the mirror flag set by `DeerHerdHandler` (line 158)
  only once `Routine == RoutineFleeing`. The very hit that *causes* the flee lands in
  the combat phase **before** the next AI tick promotes the routine → that first hit's
  flinch is NOT suppressed, and the OneShot persists into the flee.
- Net: deer starts running with a BlockReact override rendering the Idle clip →
  **idle-frame slide for ~0.85s at flee start**. Matches the report exactly.

Minimal targeted fixes (if we just want the deer fixed): add the same gates the flinch
has (Fleeing/Routing/moving) to the miss-dodge site + a shared reaction cooldown; make
`ApplyHitReactAnim` also gate on "unit is moving" rather than only the Fleeing mirror.
But see §4 — the structural fix closes the whole class.

---

## 2. Architecture review — what exists and how it measures up

### What we have (post the anim_combat_review fix rounds)
- **Two channels + one arbiter**: `RoutineAnim` (AI, pri 0-1, persistent) vs
  `OverrideAnim` (engine, pri 1-3, temporary), resolved once per frame in
  [AnimResolver.Resolve](../Necroking/Render/AnimResolver.cs#L33). Encapsulated writes
  (`OverrideAnim` internal set), `OverrideHandle` ownership, OneShot/Hold/TimedHold
  lifecycle, same-priority-until-started replacement rule, edge flags. This *is* the
  industry-standard hybrid: locomotion derived from velocity (pull), discrete actions
  as prioritized requests through a single choke point (push).
- **Existing anti-twitch**: flinch refractory 0.6s + fleeing suppression + show 0.35s
  (flinch only); trample dodge `DodgeTimer`; same-priority frame-0 protection.
- **Existing movement plants** (engine stops movement during combat anims):
  `PendingAttack` pin, `PostAttackTimer`, `InCombat` velocity zero
  ([Simulation.cs:1693-1698](../Necroking/Game/Simulation.cs#L1693)), `Incap.IsLocked`,
  jump phases. These satisfy Rule 1 in the *forward* direction (anim plays → movement
  stopped).

### The structural gap (the audit's headline)
**The inverse of Rule 1 has no mechanism.** Nothing enforces "if the unit IS moving,
don't accept an animation that doesn't stop movement."
[`AnimController.IsMovementLocked`](../Necroking/Render/AnimController.cs#L1097) — the
exact classification needed — **exists with zero callers**. Every plant above is a
per-system one-off; every *new* override writer (dodge, flinch, future stagger, buff
poses) must independently remember to gate on movement, and two already forgot (§1a,
§1b). This is hand-written discipline where a structural mechanism belongs — the same
diagnosis [ai-architecture-review.md](ai-architecture-review.md) reached for routine
liveness.

### Industry research (full agent report summarized; sources at bottom)
The 3D answer (layered/additive per-bone animation — hit reacts play on the upper body
while legs keep the run cycle, so sliding is *impossible by construction*) does not
translate to whole-body sprite frames. The sprite-era answers, which do:
1. **One arbiter, priority ladder + per-state interrupt rules** — we have the arbiter;
   our ladder is too coarse (see §3-B1).
2. **"Full-body overrides own movement"** — the invariant: *no state may suppress the
   locomotion animation without also suppressing/owning locomotion movement*. Exactly
   the user's Rule 1. Enforce structurally, not per-call-site.
3. **Reaction gating**: react cooldowns / diminishing tiers (full react → flinch →
   cosmetic-only); equal-tier re-entry *refreshes the timer, never restarts the clip*;
   poise/hyper-armor so committed attacks can't be visually interrupted.
4. **Cosmetic tier replaces 3D's "additive" layer**: when a reaction is gated
   (refractory, mid-attack, moving), feedback still fires as hit-flash tint, 2-8 tick
   hitstop on attacker+victim, particles, 1-2px shake — stacks on any clip.
5. **Attacker-side pacing** (DOOM attack tokens, kung-fu circle): cap concurrent
   attackers per target so reactions are rare by construction; waiting attackers
   circle/strafe (the idle window is *active* animation).
6. **AI sets intent, engine derives/arbitrates**: AI never names clips directly, it
   reports facts (velocity, "attacking X"); locomotion derived from actual velocity
   every frame. We already do this — `SetLocomotionAnim` derives gait from Velocity;
   good.

---

## 3. Audit — all AI↔engine interaction paths that can produce counter-intuitive visuals

Writer census is in `docs/locate-behavior/animation.md`; below is each path judged
against the two rules. ✅ = conforms, ❌ = violates, ⚠️ = fragile.

### A. While moving (Rule 1)
- **A1 ❌ Melee-miss Dodge** — §1a. No fleeing/moving gate, no cooldown.
- **A2 ❌ First-hit flinch racing flee promotion** — §1b. Phase-order race: combat phase
  flinches before the AI phase promotes to Fleeing.
- **A3 ❌ Missing-clip silent Idle fallback** — any accepted override whose clip the
  sprite lacks renders Idle while the resolver believes the override is playing
  (BlockReact/Block on deer today; any buff `HoldAnim` per old review §5.4). The render
  lies about the state, and OneShot duration becomes "length of the Idle clip".
- **A4 ⚠️ Stale-override cancel only covers Attack1-3** —
  [Game1.Animation.cs:547-556](../Necroking/Game1.Animation.cs#L547) drops a stale
  attack override once the unit moves; Dodge/BlockReact/Stunned/etc. rely purely on
  OneShot auto-expiry. Works today, but any Hold-kind or long-clip override that slips
  through while moving slides for its full duration with no backstop.
- **A5 ⚠️ Pre-roll + InCombat plant vs a unit whose handler flees without clearing
  `EngagedTarget`** — engine plants any non-player `InCombat` unit and re-stamps attack
  pre-roll frames ([Game1.Animation.cs:517-539](../Necroking/Game1.Animation.cs#L517)).
  Live deer are exempt (`ai: FleeWhenHit` blocks `EngagedTarget` assignment in
  DamageSystem), and DeerHerd clears it in `OnRoutineExit(FightBack)`. But any archetype
  that enters a flee/rout while `EngagedTarget` is set gets *frozen mid-flee doing
  attack windups* whenever the enemy is in melee range. Verify morale `Routing` clears
  engagement; make "flee ⇒ disengage" a transition-choke-point guarantee (pairs with
  ai-architecture-review R1/R2).
- **A6 ✅ Knockback / knockdown / death / jump** — all own or zero movement
  (PhysicsSystem, `Incap.IsLocked`, death, JumpSystem) before forcing anims. These are
  Rule 1's legal interrupters and they conform.
- **A7 ✅ Trample dodge-hop** — sets the Dodge anim AND owns the displacement (lerped
  hop) AND has a cooldown (`DodgeTimer`) AND suppresses the standard dodge anim at the
  miss site (`suppressDodgeAnim`). The model citizen — the pattern A1 should follow.

### B. Stationary combat choreography (Rule 2)
- **B1 ❌ A hit can visually cancel an in-progress attack swing** — flinch and attack
  are both Priority 2 (`AnimRequest.Combat`); same-priority replacement is allowed once
  the current override has started. So a landed hit mid-swing replaces the attack anim
  with BlockReact (damage still resolves via edge flags, but the swing disappears).
  Contradicts the user's stated rule "attack animations take priority over any got hit
  or avoid or block animations." The refractory only limits *how often*, not *whether*.
- **B2 ✅ Dodge deliberately below attack** — Priority 1 with an explicit comment
  ([Simulation.cs:3150](../Necroking/Game/Simulation.cs#L3150)): a defender mid-swing
  doesn't cancel its own attack to dodge. Matches the rule.
- **B3 ❌ No dodge/block cooldown** — the user believes "block or dodge animations have
  a cooldown to them being played"; only the *flinch* has one (0.6s refractory). The
  miss-dodge can restart every whiff — surrounded-unit twitch is currently only
  throttled by attacker swing timing.
- **B4 ⚠️ Equal-tier re-entry restarts the clip** — a second flinch/dodge that passes
  the gates restarts frame 0 rather than refreshing a timer (industry anti-twitch rule:
  refresh, never restart).
- **B5 ⚠️ Block anim unused on the archetype path** — legacy path plays Block during
  `PostAttackTimer`; no archetype writer ever requests Block. Deer lacking the clip is
  consistent with that, but "block" choreography is effectively absent for archetype
  units — decide whether Block is dead concept or missing feature.
- **B6 ✅/⚠️ Pacing** — idle windows come from weapon cooldowns + pre-roll + the
  InCombat plant; there is no target-side cap on simultaneous attackers (DOOM-style
  tokens). Fine at current unit counts; revisit if surrounded-unit choreography reads
  as spam.

### C. Cross-phase ordering (the race generator)
Frame order: AI phase (writes RoutineAnim, routine flags) → movement → combat phase
(damage, overrides) → `Game1.UpdateAnimations` (pre-roll/cancel, Resolve). Any gate
that reads an AI-phase flag (`Fleeing`) about an event generated in the combat phase is
one tick stale — A2 is the live instance. Prefer gating on *physical* facts available
in-phase (velocity, PreferredVel) over routine mirrors; or emit the flinch/dodge
*request* and let the resolver (which runs last) apply the gates.

---

## 4. Recommendations (prioritized)

**R1 — Enforce Rule 1 in the resolver (structural; closes A1-A4 as a class).**
In `AnimResolver.SetOverride` (single choke point, has the `Unit`): classify each
`AnimState` as *movement-locking* (attack/dodge/block/flinch/stun/knockdown… — wire up
the dead `IsMovementLocked`, extend as needed) vs *locomotion-compatible*. If the
request is movement-locking, the request's priority is below the interrupters' tier,
AND the unit is actually moving (`Velocity`/`PreferredVel` above the walk threshold)
and not already planted (`InCombat`/`PendingAttack`/`PostAttackTimer`/incap/jump), then
**reject the override** (return `OverrideHandle.None`) — optionally routing it to the
cosmetic tier (R4). Knockback/knockdown/death pass automatically because their systems
zero velocity first. One if-block; every current and future writer inherits the rule.

**R2 — Reaction lane with a shared cooldown (closes B1, B3, B4).**
Make Dodge + BlockReact a "Reaction" tier at Priority 1 (below Combat=2) sharing one
`ReactionCooldownTimer` (generalize `FlinchRefractoryTimer`, ~0.6-1.0s). Effects:
attacks can no longer be visually cancelled by hits (B1 — matches the user's rule);
dodges get the cooldown the user assumed existed (B3); a reaction that fires while one
is active *refreshes* rather than restarts (B4). Deer miss-dodge then auto-throttles
even before R1.

**R3 — Missing-clip policy (closes A3, old review §5.4).**
`SetOverride` (or Resolve on first application) checks the controller actually has a
clip for the state; if not, reject the override (+ cosmetic tier, + one dev-log line
per unit-type/state pair). Kills every "unit looks idle while the resolver thinks it's
reacting/stunned/blocking" case. Follow-up (data hygiene): decide Block/BlockReact
status per sprite — author the clips or stop requesting them for those units.

**R4 — Cosmetic feedback tier (sprite-era replacement for additive layers).**
When R1/R2/R3 suppress a reaction, still show feedback that composes with any clip:
2-6 frame white/red flash (ColorUtils tint exists), optional 2-8 tick hitstop on
attacker+victim, dust/impact particle. Suppression should never read as "the hit did
nothing." This also upgrades the already-shipped flee-flinch suppression, which
currently shows nothing.

**R5 — Flee ⇒ disengage as a transition guarantee (closes A5).**
At the routine-transition choke point (ai-architecture-review R1/R2), entering any
flee/rout routine clears `EngagedTarget` (and thus InCombat next derive) so the
pre-roll/plant machinery can't pin a fleeing unit. Verify morale `Routing` already does
this; today it's per-handler discipline.

**R6 — (Later, optional) attacker tokens for pacing (B6).**
Per-target concurrent-attack cap with waiting attackers circling/strafing. Only if
surrounded-unit fights start reading as spam; current cooldown-based pacing is
adequate at present unit counts.

Suggested order: R2 (small, immediate deer relief) → R3 (small) → R1 (the structural
one; add a regression scenario: fleeing deer under repeated whiffs + hits never shows
a non-locomotion state while `Velocity > walk threshold`) → R4 polish → R5 with the
next AI-architecture pass → R6 someday.

---

## Implementation log

- **2026-07-04 — R1 + R2 + R3 shipped** (this session): `AnimRequest.Reaction` tier
  (priority 1) for Dodge + BlockReact; `ReactionCooldownTimer` (renamed from
  FlinchRefractoryTimer) shared by flinch + miss-dodge + trample hop; miss-dodge
  funneled through `DamageSystem.ApplyDodgeAnim` (same gates as the flinch); R1
  movement gate + R3 missing-clip drop (log once) in `AnimResolver`. New scenario
  `deer_flee_no_slide` PASSES (sanity dodge accepted standing; 0 slide frames under
  hits+whiffs+raw-bypass while fleeing). Suite: 8/9 pass.
- **PRE-EXISTING failure found: `chase_attack_anim`** fails on clean master
  (verified via baseline worktree at 2278e55): `chaseFrames=0, atkFrames=426` — the
  zombie deer attacks continuously while its dummy target sits 6 tiles away and
  never chases. Suspect a melee-range/size data regression (MeleeRangeUtil.Compute
  returning ≥6 for ZombieFemaleDeer vs soldier?) — same family as the Boar size
  finding in movement-systems-review §4. **Investigate as its own task.**

## Research sources (key)
DOOM 2016 attack tokens (Game Developer "The AI of DOOM 2016"); Game AI Pro ch.28
"Beyond the Kung-Fu Circle"; "Enemy design and enemy AI for melee combat systems"
(Game Developer — GoW3 withholds attacks during player stagger); Dark Souls poise
(react gating); Capcom beat-'em-up hitstop + fixed hitstun that *extends not restarts*;
Unreal layered blend per bone / montage slots (the 3D solution we can't use, and its
handle/request-ID lifecycle which AnimResolver already mirrors); Overgrowth GDC 2014
procedural poses; Valve Source `AddLayeredSequence(priority)`; Game Programming
Patterns "State". Full annotated list in the research agent output if needed —
principles distilled in §2.
