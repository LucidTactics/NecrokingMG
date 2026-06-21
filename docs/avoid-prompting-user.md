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
[.claude/settings.json](../.claude/settings.json). It governs **Bash only** — Bash is
where the needless prompts come from; every other tool (file edits, MCP, WebFetch,
Skill, the dev-preview tools, …) is left completely alone and follows the normal
permission flow. The hook has two layers: the targeted redirects below (Layer 1) and a
deny-by-default for Bash (Layer 2, see next section). Adding a Layer-1 case = adding a
rule there plus (usually) an `allow` entry, then a section below. Behavior is covered by
[tools/hooks/test_prompt_guard.py](../tools/hooks/test_prompt_guard.py)
(`python3 tools/hooks/test_prompt_guard.py`) — run it after touching the hook.

## Layer 2: deny Bash by default — block any Bash command that would prompt

On top of the targeted redirects, the hook **denies Bash by default**: any Bash command
that would otherwise reach the user as an approval prompt is thrown back to Claude
instead. The same escape hatch applies (re-send the identical command → it prompts), so
nothing is ever permanently blocked. A Bash command is let through **without** a prompt
only if it is:

- **fully allow-listed** — every sub-command of the (possibly compound) command matches
  a permission allow rule (`.claude/settings.json` / `settings.local.json`). The hook
  splits the command on `&&`/`||`/`;`/`|`/newlines (quote-aware) and, if *all* parts are
  allowed, **force-allows** the whole thing so the user isn't prompted. This matters
  because Claude's own permission system prompts on `;`-chained compounds even when each
  part is individually allowed — e.g. `cd x; git status; echo done; python -m py_compile
  f.py`. A command containing a substitution or heredoc (`$(…)`, `` `…` ``, `<(…)`,
  `<<`) is **not** force-allowed — the hook can't safely tokenise it, so it defers to the
  normal permission flow instead. Likewise, if any segment matches a `deny` rule the hook
  won't force-allow (it respects the deny-list);
- explicitly **whitelisted as "OK to prompt the user"** via `rule_intended_prompt(…)` —
  these are prompted *immediately* instead of bounced.

Everything else (a non-allow-listed command like `taskkill …`, a raw `curl …`, or a
compound where even one segment isn't allowed) is denied with the hatch.

**Aggressive by design.** This layer would rather bounce a harmless command — one
re-send away from running — than let a needless prompt slip through. That's the point.
The cost is that it *will* occasionally get in the way. **When it does, fix the hook.**
The usual fix is an `allow` rule in `.claude/settings.json` (so the command passes
silently) or a `rule_intended_prompt` branch (so it prompts immediately instead of being
bounced). Don't be shy about adding these — it's the intended tuning mechanism, not a
sign something's broken.

**Other culprits later.** Only Bash is gated for now. If another tool turns out to be a
similar culprit, add its name to `DENY_DEFAULT_TOOLS` in the hook (and widen the
`matcher` in `.claude/settings.json` to include it) — that's the single extension point.
File edits are the canonical example of a tool you should *never* gate this way: a file
edit has no no-prompt alternative, so bouncing one is pointless friction.

**Growing the whitelist.** `rule_intended_prompt` starts empty on purpose — *for now it
blocks everything that would go to the user*. As cases come up that we agree genuinely
warrant the user's approval, add a branch there so they prompt immediately. When
`bypassPermissions` or `plan` mode is active the hook defers entirely — those modes own
their own gating.

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
