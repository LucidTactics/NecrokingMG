# North Star: Satisfaction

> Design philosophy authored by the project's collaborator. Moved out of `CLAUDE.md`
> (where it loaded every session) into this doc — kept intact for him to decide its
> future. Read it when designing or refining a player-facing system.

This is the lens for designing and refining **every** system — combat, spells, reanimation, crafting, automation. Before building or changing a system, ask: *does this satisfy?*

**Satisfaction = anticipation that gets rewarded.** The build-up is a promise; the payoff has to keep it, and keep it in proportion. A sword's wind-up makes the strike land harder; a fireball's travel builds toward its impact.
- Fireball that ignites enemies and hurls their bodies back → the payoff honors the anticipation. Deeply satisfying.
- Fireball that ticks a little HP and kills nothing → the anticipation was a lie. Flat.

**Anticipation runs on real-world intuition.** Players unconsciously predict outcomes by analogy to the physical world: heavy things hit hard, fire spreads and lingers, explosions throw bodies, a big swing carries weight. When a system behaves the way reality "should," the payoff feels *earned*. When it violates that intuition — a massive blow that moves nothing — it feels fake, no matter how correct the math underneath is.

**Cut the boring and the annoying.** Tedious, repetitive, or fiddly actions are never satisfying however well-engineered. If a task is a chore, make it feel good or automate it away.

**Protect pacing; design out frustration.** Some grind is fine, but the player's time is precious. Learning should be fun, and big moments must not bog down — never make a large battle crawl by waiting on units' animations in sequence (the AoW4 problem). Keep the density of satisfying events high.

**The test for any feature:** What anticipation does it build, and does the payoff honor that anticipation — visibly, physically, audibly? A system that computes a correct result but doesn't *show* the consequence will feel flat regardless of how solid the simulation is.
