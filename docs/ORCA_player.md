# Problems with ORCA steering for direct player control



Using **ORCA** (Optimal Reciprocal Collision Avoidance) for a player character’s direct input is usually a recipe for frustration. It is a fantastic local navigation algorithm for autonomous agents, but it fundamentally clashes with what a human expects when pushing an analog stick or pressing WASD.

Here is a breakdown of why it breaks down for direct control, how you can hybridize your system, and what the best path forward is.

---

## The Weaknesses of Pure ORCA for Direct Player Control

ORCA operates on a philosophy of **reciprocity**: it assumes every moving entity shares equal responsibility for avoiding collisions. When you force a player character into the ORCA simulation, you introduce several glaring game feel issues:

* **Loss of Agency (The "Hijacked" Input):** The most frustrating flaw. If you push the analog stick directly forward, but an NPC is walking toward you, ORCA will modify your player's velocity vector to sidestep the NPC. The player experiences this as the game engine wrestling control away from them.
* **Sluggishness and Micro-Oscillations:** ORCA calculates a set of linear constraints (half-planes) in velocity space to find the closest safe velocity to your "preferred velocity." In tight spaces or dense crowds, this math can cause the player to shudder, slow down abruptly, or violently zig-zag just because the crowd around them is shifting.
* **Poor Handling of Static Obstacles:** Standard ORCA is notoriously short-sighted with static geometry (walls, pillars, crates). It reacts to them only at the last second, meaning a player trying to hug a wall will constantly feel pushed away or stuck as the algorithm tries to parse the wall as a collision threat.
* **The "Unstoppable Force" Problem:** If the player wants to aggressively push through a crowd of enemies or citizens (like in *Assassin's Creed* or *Hitman*), ORCA will force the player to actively route around them instead of shoving them out of the way.

---

## Mixing Methods: Giving NPCs ORCA While the Player Uses Another System

**Yes, you absolutely can mix them, and this is standard practice in game development.**

To make this work, you treat the player character as an **Asymmetric Agent** within the simulation. You use a standard kinematic or physics-based Character Controller for the player, but you still project the player’s presence into the ORCA world so the NPCs can react to them.

Here is how you structure that relationship:

### 1. The Player is a "Bully" (Infinite Mass / Zero Responsibility)

In the ORCA velocity calculations, every agent expects the other to change their velocity by 50% to avoid a crash. You change the rule for the player: **The player takes 0% responsibility, and the NPCs take 100%.**

* The player moves exactly where the joystick dictates.
* You still pass the player's current position, radius, and *velocity* into the ORCA simulation loop.
* The NPCs see the player as a moving obstacle with "infinite mass." The ORCA algorithm forces the NPCs to do 100% of the heavy lifting to get out of the player's way.

### 2. Handling Hard Physical Collisions

Because the player is ignoring ORCA constraints, they might still clip into an NPC if they run directly into them faster than the NPC can step aside.

* **The Fix:** Use standard physics colliders (like a Capsule Collider or Character Controller component) to handle hard, physical body-blocking. If the player runs flush into an NPC, the physics engine stops them from clipping, while the NPC's ORCA brain rapidly scrambles to path *around* the player's new static position.

---

## The Final Recommendation

If you are building a game that requires high crowd density but also sharp, satisfying player movement, here is the architecture you should use:

1. **For the Player:** Use a standard **Kinematic Character Controller** (driven by direct stick/WASD input). Do not let ORCA alter the player’s velocity.
2. **For the NPCs:** Use **ORCA for local collision avoidance**, but feed their "preferred velocities" using a global pathfinder like **A* or NavMesh**.
3. **The Bridge:** Insert the player into the NPC’s ORCA simulation loop as an un-movable obstacle with a velocity vector.

This hybrid approach gives you the best of both worlds: the player enjoys razor-sharp, immediate responsiveness, while the crowd realistically parts like the Red Sea as the player walks through them.