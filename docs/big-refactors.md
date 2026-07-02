# Big Multi-Agent Refactors (ultracode / Workflow)

Reference for running large, mechanical, repo-wide refactors by fanning out many
subagents (one per file) via the `Workflow` tool ("ultracode"). Written after the
first real attempt — a "hoist repeated `Units[i]` index access into one local
`Unit` reference" sweep across ~80 files — which *technically* worked but was
sabotaged by a tooling mistake. The transform was fine; the **orchestration**
is where it went wrong. This doc is about the orchestration.

## When this pattern fits
A refactor is a good ultracode candidate when it is:
- **Local** — each edit is decided from a single file, no cross-file context needed.
- **Mechanical but judgement-heavy** — too tedious/large for one context, but each
  site needs a small correctness judgement a regex can't make (so not a `sed` job).
- **Independently partitionable** — one agent per file, files don't depend on each other.

Examples: hoisting repeated accessor calls, renaming a local idiom, adding null-guards,
migrating a call style. Counter-example: anything requiring a signature change whose
callers span files (that needs LSP-driven caller enumeration first — see
[docs](../CLAUDE.md) / the LSP-for-refactors memory — and usually isn't purely local).

## The failure that happened (read this first)
Symptom: the workflow self-reported **"80 files, 342 hoists, build clean, 0 errors."**
Reality: only **12 files** were actually modified on disk; **70 files of edits were
stranded in `git stash@{0}`**.

Root cause: agents were spawned as `general-purpose` (full Bash). One agent decided to
run `git stash` to "isolate its work" before editing. But **all ~14 concurrent agents
share ONE working tree**. That `git stash`:
1. Swept every *other* agent's in-flight edits into the stash.
2. Its `git stash pop` hit a conflict and aborted, leaving everything stashed.
3. Agents that ran *after* the stash wrote fresh edits to the now-reverted tree.

Result: the real output was split between a mid-run stash snapshot and the post-stash
working tree, with overlap — **not safely reconcilable by hand**. And the workflow's
final `dotnet build` ran against the 12-file partial, so "build clean" was a **false
green**.

### Why "build clean" lied
The build phase compiled whatever was *on disk at the end*, which was the corrupted
partial. A partial set of valid edits still compiles. **A green build proves the
on-disk subset compiles — it does NOT prove the refactor is complete or that the
intended edits are present.**

## Rules for authoring the workflow

### 1. Forbid git and builds in EVERY agent prompt
Agents share the tree. In the transform prompt (and the fix prompt), include an explicit
FORBIDDEN block, with the reason:
> You are one of ~14 agents editing DIFFERENT files in the SAME shared working tree at the
> same time. NEVER run any git command (stash/checkout/reset/add/commit/restore/clean) and
> NEVER run `dotnet build`. Use ONLY Read and Edit on your one assigned file. A separate
> phase builds the whole project; a fix agent handles any compile errors.

Saying *why* matters — an agent told only "don't use git" may still reach for it under
pressure; an agent told "git stash will corrupt 13 other agents' work" won't.

### 2. Or isolate: one worktree per agent
Alternative to trusting the prompt: pass `isolation: 'worktree'` to `agent()` so each gets
its own git worktree and physically can't stomp others. Costs ~200-500ms + disk per agent
and complicates collecting results back into one tree, so for *distinct-file* edits the
"forbid git" approach is simpler and was sufficient. Reach for worktrees when agents might
touch the *same* file.

### 3. Give the transform a struct-vs-class-aware safety policy
This refactor is only semantics-preserving because `Unit` is a **class** (`Units[i]`
returns a reference; `Units` and `UnitsMut` are the same object). Several agents *guessed*
it was a struct — and stayed correct only because they applied a conservative "never hoist
across an intervening write or membership mutation" rule, which is safe either way. Bake
that conservatism into the prompt: **when unsure, skip the site.** Coverage < correctness.

Domain landmines the prompt must call out for *this* codebase:
- **`idx` is a slot index, NOT `unit.Id`.** They are different values. Never substitute one
  for the other. An index passed to a function stays the raw integer index.
- **Swap-and-pop** (`UnitArrays.RemoveUnit`) means a slot's occupant can change after any
  add/remove/kill/spawn call — so don't hoist a single captured reference across such a call.
- Short-circuit guards (`idx >= 0 && Units[idx]...`, `idx >= 0 ? Units[idx] : ...`): don't
  hoist the access above the guard or you index with a negative/stale slot and throw.

### 4. Structure: Transform → Build → Fix loop
- **Transform**: `parallel`/`pipeline` one agent per file, each returning a small structured
  result (`{file, hoists, skipped, notes}`).
- **Build**: ONE agent runs `dotnet build` and returns structured errors.
- **Fix**: if errors, `parallel` one agent per error-file with the exact compiler messages;
  allow it to revert an individual bad edit rather than force a fix. Loop ≤3 times.

### 5. Filter the file list cheaply
Only include files that *can* have the pattern (e.g. `grep -c` the accessor; a file with a
single occurrence has no repeat to hoist). Saves dozens of no-op agents.

## Verification discipline (do NOT trust the workflow's summary)
After any big refactor workflow, verify on disk *yourself* before believing it:
1. **`git status --short | wc -l`** — does the modified-file count roughly match the claimed
   number of changed files? A large mismatch = something ate the edits (stash, revert, crash).
2. **`git stash list`** — any unexpected stash means an agent touched git. Investigate before
   trusting anything.
3. **Re-run `dotnet build` yourself** — never rely on the workflow's self-reported "clean."
4. **Spot-read a couple of diffs** (`git diff <file>`) to confirm the transform looks right,
   including one of the big files.
5. Only then offer to commit. Per repo policy: build must pass before any push, and always
   ask before pushing (the tree is shared with a Drive-syncing collaborator).

## Recovery when a run corrupts the tree
The session started clean (verified: `git status` clean at start), so every change was the
run's own output — making reset safe:
1. `git reset --hard HEAD` — discard the corrupted partial. (Only safe because there was no
   pre-existing uncommitted work. Check first.)
2. Keep any orphaned `stash@{0}` as a safety net until the re-run is verified, then
   `git stash drop`.
3. Fix the workflow script (forbid git/builds), then **re-run FRESH** — not
   `resumeFromRunId`. Resume returns agents' cached *text results* WITHOUT re-applying their
   file edits to disk, so a resumed run against a reset tree produces an empty tree with a
   happy-looking summary.

## Cost / quota note
Concrete numbers from the reference run (the `Units[i]` hoist), so future jobs can be
extrapolated:

| Metric | Value |
| --- | --- |
| Files in the job | **80** `.cs` files (every file with 2+ accessor occurrences) |
| Total size of those files | **~35,150 LOC** |
| Agents spawned | **81** (80 transform, one per file, + 1 build; fix agents on top if the build breaks) |
| Subagent tokens | **~3.2M** |
| Wall-clock | **~27 min** |
| Quota consumed | **~50% of a 5-hour quota** |

Rules of thumb from that data point: **one agent per file**, roughly **~40k subagent tokens
per file** and **~90 tokens per LOC** processed. So a job of N files of similar size costs on
the order of `N × 40k` tokens; ~80 files of mixed C# ≈ half a 5-hour quota. Budget accordingly:
run when you have headroom, and if quota is tight, scope to the **high-value files first**
(core sim + AI + renderers — where the repeated-access density is highest) and leave the
scenario tests (many small files, low yield) for a later pass. Filtering out single-occurrence
files (see rule 5) is the cheapest way to cut agent count.

## Pre-flight checklist
- [ ] Working tree clean (`git status`) — so recovery-by-reset is safe.
- [ ] File list filtered to files that can contain the pattern.
- [ ] Transform prompt: FORBIDDEN git/build block **with the reason**.
- [ ] Transform prompt: conservative "skip when unsure" + the domain landmines (idx≠Id,
      swap-and-pop, short-circuit guards).
- [ ] Fix prompt: same FORBIDDEN block; may revert individual edits.
- [ ] Build + Fix loop present.
- [ ] Post-run: verify `git status` count, `git stash list`, own `dotnet build`, spot diffs
      — before trusting the summary or committing.
- [ ] Quota headroom for a multi-million-token run.
