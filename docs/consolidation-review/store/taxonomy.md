# Method Labeling Taxonomy (Necroking codebase duplicate-hunt)

You label C# methods with facets so semantically-similar methods cluster together
EVEN IF their code looks different. Label by INTENT, not by surface syntax.

## verb — what the method fundamentally does (pick EXACTLY one from this list)

| verb | meaning |
|---|---|
| query-nearest | find the nearest/best entity or object matching criteria (world or data) |
| query-area | find all entities/objects in a radius/rect/region |
| hit-test | determine what is under a point (cursor picking, UI hit-testing) |
| lookup | fetch by id/name/key from a registry, dict, or list |
| spawn | create/instantiate an entity, object, projectile, or effect in the world |
| despawn | remove/kill/destroy an entity or object from the world |
| cast-ability | initiate/execute a spell, ability, or attack (the act of using it) |
| apply-effect | apply/remove a buff, status, heal, or modifier to a target |
| apply-damage | resolve damage, knockback, or death consequences |
| ai-decide | AI decision-making, state-machine transitions, target selection, behavior steps |
| steer-move | movement execution, steering, locomotion, facing, jumping |
| pathfind | path computation, walkability, route smoothing |
| update-tick | per-frame/per-interval system update loop driving many concerns |
| render-world | draw world-space content (sprites, terrain, effects) |
| render-ui | draw screen-space UI/HUD/editor visuals |
| ui-layout | measure, arrange, scroll, or position UI elements |
| ui-input | handle mouse/keyboard interaction with UI widgets/windows |
| world-input | handle mouse/keyboard interaction with the game world (clicks on units/ground) |
| serialize-save | write game/map/settings state to disk or a DTO |
| deserialize-load | read game/map/settings state from disk or a DTO |
| asset-load | load/resolve textures, fonts, shaders, atlases, content files |
| config-data | parse/build/register data definitions (defs, registries, stats derivation) |
| math-geometry | pure math, geometry, interpolation, color math |
| animation | animation state, frame selection, transitions, flipbooks |
| vfx | visual effect spawning/management (particles, glyphs, lightning, fog) |
| audio | sound playback/management |
| net-sync | networking send/receive/serialization for multiplayer |
| editor-op | editor-only manipulation (place/edit/undo of map or data in editors) |
| dev-debug | dev commands, logging, screenshots, diagnostics, profiling |
| inventory-item | inventory/equipment/item manipulation |
| crafting | crafting/recipe logic |
| economy-resource | player resources, costs, currencies |
| job-work | worker/job assignment and execution |
| validation-check | boolean predicate / can-do / is-valid checks |
| cache-index | build/maintain caches, spatial indexes, atlases, pools |
| event-hook | event dispatch, callbacks, trigger firing |
| timer-cooldown | cooldown/timer bookkeeping |
| procgen-random | procedural generation, randomization, noise |
| string-format | text formatting, tooltips text building, parsing strings |
| collection-util | generic collection/data-structure helpers |
| lifecycle | constructor/init/reset/dispose/window-open-close plumbing |
| other | genuinely none of the above |

## target — WHAT it operates on. 1-3 lowercase words, be consistent.
Prefer these canonical targets when applicable:
unit, corpse, env-object, item, spell, buff, projectile, tile, ground, wall, road,
zone, village, horde, squad, worker, job, resource, weapon, armor, potion,
texture, sprite, atlas, animation, font, shader, window, widget, panel, tooltip,
button, list, map-data, save-file, settings, registry, packet, camera, particle,
sound, string, color, vector, rect, path, trigger, misc

## mechanism — HOW it works. 2-5 lowercase words, freeform.
Examples: "linear scan distance", "quadtree query", "switch on enum", "grid raster scan",
"reflection over properties", "immediate-mode draw", "json roundtrip", "dictionary lookup",
"state field mutation", "delegates to system X"

## summary — one sentence (max 15 words) of the method's INTENT from a game-design view.

## dup_hint — OPTIONAL. If while labeling you notice this method looks like a
re-implementation of another method you labeled in this batch (or a famous pattern
like "find X under cursor"), write a short note. Otherwise omit or empty string.

## Output format
Write a JSON array, one object per method, EXACTLY these keys:
[{"id": <int from input>, "verb": "...", "target": "...", "mechanism": "...", "summary": "...", "dup_hint": ""}]

Rules:
- EVERY input method id must appear exactly once in your output.
- verb MUST be one of the taxonomy strings verbatim.
- Label the method's PRIMARY purpose; a draw method that also handles clicks is whichever dominates.
- Wrapper/delegation one-liners: label with the verb of what they delegate to; mechanism "delegates to ...".
