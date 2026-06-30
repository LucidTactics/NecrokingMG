---
name: locate-behavior
description: Find where a behavior or feature lives in the Necroking codebase — which files and functions are responsible, and where new code should go (incl. files to create). USE THIS (don't freestyle grep) whenever you're about to locate where code lives or where to add a feature — invoke it FIRST, before Grep/Glob, at the start of any change. Triggers — "where is X handled", "which file does Y", "where should I add Z", "what code is responsible for W", or any task that begins with finding the right files (e.g. adding a command, system, editor hook, or data path). Backed by an on-demand, self-extending per-subsystem architecture map under docs/locate-behavior/.
---

# Locate behavior in the Necroking codebase

This skill **delegates to the `locate-behavior-finder` subagent** so the architecture
map gets crunched in an isolated context — only the answer returns to this session, not
the doc text. Don't read `docs/locate-behavior/` yourself; that would defeat the purpose.

## How to run it

Spawn the finder via the **Agent tool** with `subagent_type: "locate-behavior-finder"`,
passing the user's goal/behavior **verbatim** as the prompt (add any working context
that narrows it — the file you're editing, the feature you're adding). One agent only —
_the finder does not spawn further agents._

```
Agent(
  subagent_type: "locate-behavior-finder",
  description: "locate <behavior>",
  prompt: "<what the user wants to find/achieve, plus any relevant context>"
)
```

It returns: the responsible **existing files** (path + responsibility + functions),
**where new code should go** (files to edit or create), **pitfalls**, and a note of any
map docs it created/fixed.

## After it returns

- **Relay** the located files/answer to the user (or just use them to proceed with the
  task — that's the point of locating).
- **The finder commits its own map changes** (a `docs(locate-behavior): …` commit scoped
  to just the `docs/locate-behavior/*.md` it touched) before returning, so you don't have
  to — it reports the hash in its answer. It does NOT push; surface that to the user per
  git policy. The skill, the agent, and the map are all git-tracked and shared.

## Why a subagent (background)

The map is intentionally incomplete and self-extends on demand. The finder owns that
workflow (read overview → open 1–3 area docs → verify with LSP/Grep → self-heal missing
or stale docs → answer). Running it in a subagent keeps all that reading out of the main
context; you pay only for the conclusion. Full behavior lives in
`docs/locate-behavior/README.md` (the finder's operating manual), with the agent shim at
`.claude/agents/locate-behavior-finder.md`.
