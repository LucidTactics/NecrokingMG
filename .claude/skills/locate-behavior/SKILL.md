---
name: locate-behavior
description: Find where a behavior or feature lives in the Necroking codebase — which files and functions are responsible, and where new code should go (incl. files to create). Use when asked "where is X handled", "which file does Y", "where should I add Z", "what code is responsible for W", or when starting a change and you need to find the right files fast. Backed by an on-demand, self-extending per-subsystem architecture map under reference/.
---

# Locate behavior in the Necroking codebase

Given a description of some behavior/feature, find the **existing files & functions
responsible** for it, and **where new code should go** — without loading the whole
architecture into context every session (that's why this is a skill and not in
CLAUDE.md).

## Workflow (progressive disclosure — read only what you need)

1. **Read `reference/overview.md`** — the routing map. It lists the subsystems, a
   behavior→area index, and which `reference/<area>.md` doc to open. It also marks
   which areas are **documented** vs **not yet documented**.
2. **Open only the 1–3 `reference/<area>.md` docs** the query points to. Do **not**
   read the whole `reference/` folder — defeats the purpose.
3. **Verify against live code.** The docs name files and symbols but can lag the code.
   Use Grep / Glob / the LSP tool (`workspaceSymbol`, `documentSymbol`) to confirm a
   symbol still exists and to get its **current** location — the docs deliberately omit
   line numbers because they rot.
4. **Answer** with:
   - the relevant **existing** files (path + what each is responsible for + the
     specific functions involved),
   - for new work: **where it should go** — which file(s) to edit or **create**, and
     what each new piece should contain (key functions / where the code belongs).

## Self-healing — keep the map correct (IMPORTANT)

The map is **intentionally incomplete and grows on demand.** Whenever you use this
skill and the docs are **lacking, wrong, or missing the area you need:**

- **Missing area** — if `overview.md` routes you to an area with **no
  `reference/<area>.md`** (or marked "not yet documented"), do the research now:
  explore those files with Glob/Grep/LSP/Read, then **write `reference/<area>.md`**
  following the format of the existing docs. Use
  [`reference/game1-partials.md`](reference/game1-partials.md) as the worked template.
- **Wrong/stale doc** — if an existing doc names symbols that were renamed, moved, or
  removed, or describes a responsibility that has shifted, **fix the doc** as part of
  your task.
- After adding or correcting a doc, **update `overview.md`** — its subsystem table,
  the behavior→area index, and the documented-vs-not list.

Leave the skill better than you found it. Over time it comes to document the whole
project — each session pays only for the area it touches.

## Reference-doc format

Each `reference/<area>.md` documents one subsystem/folder. For each file: its path, a
short "what lives here", the key types/functions **by name (no line numbers)**, and a
"**look/edit here when…**" line that maps concrete behaviors to that file (this line is
what makes a behavior description findable). Cross-link related areas at the end. Match
[`reference/game1-partials.md`](reference/game1-partials.md).

## Maintenance

This skill is **git-tracked and shared** (whitelisted in `.gitignore`). Extending the
map is a normal code change — commit the new/edited `reference/*.md` with your work so
collaborators get the improved map.
