## Docs Directory (`docs/`)
On-demand **reference** material: stable, still-useful info that doesn't need to be
in CLAUDE.md (and thus in context) every session — deep how-to guides and subsystem
references. Unlike `todos/`, these don't get deleted when "done"; they're looked up when
relevant. When something in CLAUDE.md is good to keep but no longer a primary driver, move
it here (or make it a skill if it's an invokable procedure) and leave a one-line pointer in
CLAUDE.md. Current contents:
- [north-star.md](north-star.md) — the design philosophy ("does this satisfy?").
- [code-map.md](code-map.md) — folder-by-folder map of the C# code: what each folder is for, where new code goes, namespace exceptions.
- [git-discipline.md](git-discipline.md) — the *why* behind the Drive-sync git reflexes + the `user settings/` migration.
- [avoid-prompting-user.md](avoid-prompting-user.md) — how the Bash prompt-guard hook works and how to tune it (whitelist methods + reminder hooks).
- [spells.md](spells.md) — deep reference behind the `add-spell` skill.
- [devpreview.md](devpreview.md) — deep reference behind the `drive-game` skill (the three interface tiers in detail).
- [testing-scenarios.md](testing-scenarios.md) — deep reference behind the `test-scenario` skill.
- [locate-behavior/](locate-behavior/) — the architecture map + finder operating guide behind the `locate-behavior` skill (moved out of `.claude/` so the finder can self-update it without write prompts).
- [big-refactors.md](big-refactors.md) — how to run large multi-agent (ultracode/Workflow) refactors safely: what goes wrong (a stray `git stash` in the shared working tree corrupted a run), the forbid-git / verify-on-disk discipline, and a pre-flight checklist.
- [known-platform-bugs.md](known-platform-bugs.md) — framework/OS bugs we've hit + the workaround in use (e.g. MonoGame's `IsActive` is stale when launched unfocused → poll `WindowChrome.IsForegroundWindow()`); check before fixing a symptom that smells like the engine lying.
- [OutOfContext_Skills/](OutOfContext_Skills/README.md) — heavyweight, **explicitly-user-invoked** procedures deliberately NOT registered as skills (zero per-session context cost). When the user asks to run one (e.g. "re-run the dup review"), open that README's index and follow the procedure.

Canonical implementations of common patterns are tracked in [standard_patterns.md](standard_patterns.md). Consult this when starting work that might overlap with an existing solution. Update it when a new standard is established.
