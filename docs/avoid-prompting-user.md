# How to avoid prompting the user for things that shouldn't need approval

Some Bash commands Claude runs make the user click "approve" for no good reason —
searching the codebase (`grep`), inspecting a file (`cat`), checking that a script
parses (`python -m py_compile`). These are safe, read-only, high-frequency actions.
Every needless prompt is friction. This doc is the playbook for eliminating them.

## Why a CLAUDE.md instruction is not enough

CLAUDE.md already says "prefer the Grep/Glob/Read tools over bash `grep`/`find`/`cat`."
That helps, but **an instruction in CLAUDE.md is far from guaranteed to be followed.**
It's just text in the context window: it competes with everything else, it's easy to
forget mid-task, and there's no mechanism that catches a lapse. Relying on Claude to
*remember* a rule on every single tool call is not reliable.

What *is* reliable is a **hook** — code the harness runs deterministically. So the
strategy is: let a `PreToolUse` hook **remind Claude once, at the exact moment it sends
a command the user would have to approve.** The reminder makes Claude stop and reach
for the no-prompt path instead. That single, perfectly-timed nudge eliminates most of
the false prompts that a passive CLAUDE.md line never could.

## A fix has two parts

Reminding Claude only works if there's somewhere better to send it. So every case needs
**both** of these:

1. **A whitelisted method Claude knows about.** There has to be a way to accomplish the
   task that does *not* prompt the user — either a dedicated tool (e.g. the `Grep` tool)
   or a pre-approved Bash command (an `allow` rule in `.claude/settings.json`). Without
   this, the reminder has nothing to redirect to and you've just blocked Claude.

2. **A reminder when Claude uses another method for the same job.** A `PreToolUse` hook
   that detects the prompting method and **denies it with a message** pointing at the
   whitelisted method from part 1. This is what catches the lapse the CLAUDE.md line
   couldn't. Keep it *narrow* — only fire on the clear cases, never on legitimate uses
   the whitelisted method can't cover (e.g. `grep` inside a pipeline).

The hook lives at [tools/hooks/bash_prompt_guard.py](../tools/hooks/bash_prompt_guard.py)
and is wired as a `PreToolUse` / `Bash` hook in
[.claude/settings.json](../.claude/settings.json). It holds one rule per case; adding a
case = adding a rule there plus (usually) an `allow` entry, then a section below.

## Err on denying — the escape hatch makes it safe

Rules should **lean toward denying**. A too-eager deny normally risks blocking a
command Claude genuinely needs (some shell incantation a dedicated tool can't express),
which would be worse than the prompt we're trying to avoid. So the hook builds in an
escape hatch, and every deny message ends with it:

> *If you really cannot use another method, send the exact same command again and it
> will prompt the user.*

Mechanically: the hook stores the **last denied command** (per session, in the OS temp
dir). When a command would be denied:

- If it is **byte-identical to the just-denied command**, the hook emits `ask` instead —
  the user gets the normal approval prompt — and clears the store.
- Otherwise it's a first denial: store the command and deny with the hatch line.

And crucially, the store is **always cleared/overwritten by any other command** — an
allowed command clears it, a different denied command overwrites it. So the bypass only
fires on an *immediate, deliberate* re-send; an accidental repeat later (after anything
else has run) is treated as a fresh denial and does **not** slip through. Net effect:
Claude is nudged hard toward the right method, but a real need is never more than one
re-send (and one honest user prompt) away. That safety net is what lets the rules be
broad.

## Cases handled so far

### Filesystem search & inspection — `grep` / `rg` / `find` / `cat` / `head` / `tail`

- **Whitelisted method:** the built-in **Grep** (content search), **Glob** (file
  patterns), and **Read** (`cat`/`head`/`tail`) tools. They never prompt, integrate with
  the permission UI, and return clickable file links.
- **Reminder:** the hook denies a Bash command whose **leading** token (after any
  `cd … &&` prefix) is one of those six, with a message naming the right tool. It
  deliberately does **not** fire when the command appears later in a pipeline
  (`dotnet build | grep error`, `git log | cat`) — those are legitimate and the tools
  can't do them.

### Python syntax validation — hand-rolled `ast.parse` / `py_compile`

- **Whitelisted method:** `python -m py_compile <file>` (also `py` / `python3`), added to
  `permissions.allow` in `.claude/settings.json`, so it runs without prompting. This is
  the canonical "does this script parse?" check.
- **Reminder:** the hook denies a `python -c …` that hand-rolls a syntax check
  (`ast.parse(`, `import py_compile`, `py_compile.compile(`) and points back at
  `python -m py_compile`. The whitelisted `-m py_compile` form is explicitly left alone.
