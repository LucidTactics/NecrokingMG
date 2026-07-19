# locate-behavior — finder operating guide

This folder is the **architecture map** for the Necroking codebase and the **operating
manual** for the `locate-behavior-finder` subagent. It lives under `docs/` (deliberately
NOT under `.claude/`) so the finder can read **and update** it without permission prompts.

- **[overview.md](overview.md)** — the routing map: subsystem table, behavior→area index,
  documented-vs-not list. Start here.
- **[anti-patterns.md](anti-patterns.md)** — Contains lists of anti patterns to look for and
  either log or tell the caller to fix. Read this before you start to investigate.
- **[asset-management.md](asset-management.md)** — Contains rules regarding json management etc. Always read it.
- **`<area>.md`** — one doc per subsystem/folder (e.g. [game1-partials.md](game1-partials.md),
  [render.md](render.md), [jobs-workers.md](jobs-workers.md), [dev.md](dev.md)).
- **This README** — the workflow, self-heal rules, commit discipline, doc format, and the
  answer format the caller expects.

## Workflow

1. **Read [overview.md](overview.md)** — match the request to an area.
2. **Open only the 1–3 `<area>.md` docs** the goal points to. Do NOT read the whole folder
   — that defeats the purpose. Docs + light targeted reading are your default mode.
3. **Dive deeper only when warranted** — when you must write a new doc, a doc looks
   stale/wrong, or you want to sanity-check your own advice. Then Grep/Glob/Read the real
   source and use the **LSP tool** (`workspaceSymbol`, `documentSymbol`) to confirm a
   symbol still exists and get its current location. Prefer LSP/Grep over reading large
   files end-to-end.
4. **Self-heal the map** (below) if it was lacking for this query.
5. **Answer** in the format below.

## Self-healing — leave the map better than you found it (IMPORTANT)

You have Write/Edit precisely so you can keep the map correct as a side effect of
answering. Whenever the docs are lacking, wrong, or missing the area you needed:

- **Missing area** — if overview.md routes you to an area with no `<area>.md` (or marked
  "not yet documented"), research it now (Glob/Grep/LSP/Read), then **write `<area>.md`**
  here following the format below. Use [game1-partials.md](game1-partials.md) as the worked
  template.
- **Wrong/stale doc** — if a doc names symbols that were renamed/moved/removed, or a
  responsibility has shifted, **fix the doc** as part of this task.
- After adding or correcting a doc, **update overview.md** — its subsystem table, the
  behavior→area index, and the documented-vs-not list.

## Commit your self-heal BEFORE returning (only if you changed docs)

If — and only if — you created or edited map docs this run, commit them yourself before
returning. You know best what changed and why, so you author the message. These are
documentation-only changes, so it's safe; no build is needed.

**Staging — scope to YOUR docs only. This is critical:**
- Stage ONLY the specific files you touched, each by explicit path (the `<area>.md` you
  wrote/fixed and overview.md if you updated it). Everything you change lives under
  `docs/locate-behavior/` — never stage anything outside it.
- **NEVER** use `git add -A`, `git add .`, `git add -u`, or `git commit -a`. The working
  tree may hold unrelated in-progress work from the user or another session; sweeping it
  into your commit is a serious error.
- Restrict the commit to your exact pathspecs so a pre-existing staged index can't ride
  along: `git commit -m "<subject>" -m "<body>" -- <path1> <path2> …`.

**Message — you author it.** Conventional style, e.g. subject
`docs(locate-behavior): document <area> in the architecture map` (or `fix …` for a stale
correction); a one/two-line body on which area(s)/files you covered. End the message with:
`Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.

**Do NOT push** — leave that to the user (project policy). After committing, note the short
hash + subject in your answer's "Map updates" line. If you changed no docs, do not touch
git at all.

## Reference-doc format

Each `<area>.md` documents one subsystem/folder. For each file: its path, a short "what
lives here", the key types/functions **by name (NO line numbers — they rot)**, and a
"**look/edit here when…**" line mapping concrete behaviors to that file (this line is what
makes a behavior findable). Cross-link related areas at the end. Match
[game1-partials.md](game1-partials.md) exactly.

## Constraints

- **Do NOT spawn other agents.** You are the single middle-ground finder; you crunch
  context yourself. (You have no Agent tool by design.)
- Keep file reads targeted. Big-file end-to-end reads are a last resort, justified only by
  doc-writing or a sanity check you can't do with LSP/Grep.
- Verify before you assert — if you name a symbol/file as the place to edit, you should have
  confirmed it exists (doc + a quick LSP/Grep check when in doubt).

## Answer format (this is all the caller gets)

Return Markdown:

- **Relevant existing files** — for each: `path` + what it's responsible for + the specific
  functions/types involved.
- **Where new code goes** (for additive work) — which file(s) to edit or **create**, and
  what each new piece should contain.
- **Pitfalls / gotchas** — project-specific traps for this area (drawn from the docs and
  what you saw).
- **Map updates** (only if any) — one line: which doc(s) you wrote/fixed, and the commit you
  made for them (short hash + subject).

Use clickable relative paths (e.g. `Necroking/Game1.Spells.cs`). Be concise; the value is
precision, not volume.
