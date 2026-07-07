# Out-of-Context Skills

Heavyweight, **explicitly-user-invoked** procedures that are deliberately NOT registered
as `.claude/skills/` skills. A registered skill's name+description is loaded into Claude's
context every session; procedures in this folder cost zero context until the user asks for
them by name. Use this folder for operations that are expensive (multi-hour / multi-million
token), rare, and must never fire from routine work.

**How Claude uses this folder:** when the user asks to run one of these procedures, open
the procedure's folder and follow its main `.md` end-to-end (they are written in skill
format — trigger rules, pipeline, prompts). Never invoke one of these without the user
explicitly asking for it by name/intent.

## Index

| Procedure | Invoke when the user says... | Entry point |
|---|---|---|
| **dup-review** | "re-run the duplication/consolidation review", "refresh the dup-review labels" | [dup-review/SKILL.md](dup-review/SKILL.md) |

## Adding a new one

Write it like a skill (trigger conditions first, then the procedure), put it in its own
subfolder, add an index row here. Do NOT add per-procedure pointers to CLAUDE.md — the
single CLAUDE.md line pointing at this README covers all of them.
