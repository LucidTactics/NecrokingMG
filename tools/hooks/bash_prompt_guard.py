#!/usr/bin/env python3
"""PreToolUse hook: keep Bash calls from needlessly prompting the user.

Scope: this hook governs **Bash only** (see DENY_DEFAULT_TOOLS). Every other tool —
file edits, MCP tools, WebFetch, Skill, the dev-preview tools, … — is left completely
alone and follows the normal permission flow. Bash is where the needless prompts come
from, so it's the only culprit gated for now. If another tool turns out to be a similar
culprit later, add its name to DENY_DEFAULT_TOOLS (and widen the `matcher` in
.claude/settings.json if it isn't already `*`) — that's the one place to extend.

Posture for Bash: DENY BY DEFAULT, and AGGRESSIVE BY DESIGN. Any Bash command that
would pop a user-approval prompt is thrown back to Claude instead — with an escape
hatch: re-sending the EXACT same command lets it through to a normal prompt. Because the
hatch always exists, the default can be broad without ever permanently blocking a
genuinely-needed command. See docs/avoid-prompting-user.md.

Two layers:

  1. Bash special-cases — targeted redirects that point Claude at a no-prompt
     alternative for a specific job: hand-rolled syntax check -> `python -m py_compile`.
     These fire even on otherwise allow-listed commands, because the dedicated tool is
     strictly better. (Read-only search like `grep`/`cat` is no longer special-cased — it
     falls through to the read-only fast-allow in evaluate() and just runs.)

  2. Deny-by-default — any other Bash command is thrown back UNLESS it is:
       * fully allow-listed: every sub-command of the (possibly compound) command matches
         a permission allow rule (.claude/settings*.json). The hook splits the command
         into its sub-commands and force-ALLOWS the whole thing, so the user isn't
         prompted — Claude's own permission system otherwise prompts on `;`-chained
         compounds even when each part is individually allowed (e.g.
         `cd x; git status; echo done`). Commands containing genuine
         substitutions/heredocs ($(), ``, <(), <<) are NOT force-allowed — they defer to
         the normal permission flow instead;
       * explicitly whitelisted as "OK to prompt the user" (rule_intended_prompt) -> the
         user is prompted immediately instead of the command being thrown back.
     Everything else -> deny with the escape hatch.

Structural parsing (AST-first): bashlex (a real Bash parser) via bash_ast.analyze() does
the heavy lifting — it splits the command into its individual PROGRAM INVOCATIONS and, for
each, hands back the leader (the program actually run) plus that invocation's own dequoted
args. Every structural question the guard asks rides on that:
  * "does this mutate?" — checked per invocation (file_write_detect.command_side_effect on
    each leader+args), so a write-named token riding in another command's arguments
    (`echo rm`, `grep -n kill .`) is NOT mistaken for the command itself;
  * "is this a find in its mutating mode?" — the `-delete`/`-exec` check reads THAT find's
    args, not a stray match anywhere in the string;
  * substitution/heredoc/real-file-write — `$(…)` inside single quotes is a literal (not a
    substitution) and `$(( 5 > 3 ))` is arithmetic (not a `>` redirect).
bashlex can't parse a few forms (arithmetic expansion, syntax errors). ONLY on those does
evaluate() fall back to the quote-aware whole-string heuristics in this file / in
file_write_detect (_split_segments / _has_unsafe / has_output_redirection /
has_side_effects). They're strictly the parse-failure safety net — coarser (a write-named
token anywhere reads as a writer), but deny-by-default means a failed parse degrades to the
old conservative behaviour, never to a wrong allow. That's the only reason _split_segments
et al. still exist; the primary path never touches them.

It would rather bounce a harmless command (one re-send away from running) than let a
needless prompt through. When that gets in the way, the fix is to add an `allow` rule,
a special-case redirect, or a `rule_intended_prompt` branch — don't be shy; that's the
intended way to tune it.

Re-send bypass (state machine):
  * The last denied command is stored (per session, in the OS temp dir).
  * If the very next Bash command is byte-identical to it, the hook emits `ask` (normal
    prompt) and clears the store.
  * ANY other Bash command clears/overwrites the store, so only an *immediate*
    deliberate re-send slips through; a later accidental repeat is a fresh denial.

Output: print a deny/ask JSON to act; print nothing (exit 0) to defer to the normal
permission flow. Any error -> exit 0 (never wedge the tool on a hook bug).
"""
import fnmatch
import hashlib
import json
import os
import re
import sys
import tempfile

import bash_ast
import file_write_detect

# Tools subject to deny-by-default. Add a tool here (and ensure the settings.json
# `matcher` covers it) if it becomes a culprit like Bash.
DENY_DEFAULT_TOOLS = {"Bash"}

RESEND_HINT = ("\n\nIf you really cannot use another method, send the exact same command "
               "again and it will prompt the user.")

# ---------------------------------------------------------------------------------
# Layer 1: Bash special-case redirects.
# ---------------------------------------------------------------------------------

def leading_command(cmd: str) -> str:
    """The first bare command token, after peeling any `cd <path> &&` prefixes and a
    leading subshell opener. So `cd repo && grep x` -> 'grep', `( cat f )` -> 'cat'."""
    s = cmd.strip()
    while True:
        m = re.match(r"^cd\s+[^&|;]+&&\s*", s)
        if not m:
            break
        s = s[m.end():]
    s = s.lstrip("({ \t")
    m = re.match(r"[^\s|;&<>()]+", s)
    if not m:
        return ""
    return m.group(0).rsplit("/", 1)[-1].rsplit("\\", 1)[-1]


# --- Rule: hand-rolled python syntax check -> `python -m py_compile` --------------
_PY_LEAD = {"python", "python3", "py"}

_PY_VALIDATE_MSG = (
    "Validate Python syntax with `python -m py_compile <file>` — it's pre-approved "
    "in .claude/settings.json, so it runs without prompting the user. Re-run as "
    "`python -m py_compile <file>` instead of a hand-rolled ast.parse / py_compile "
    "check."
)

# python interpreter options that consume the FOLLOWING token as their value (so the
# payload scan doesn't mistake the value for a script path).
_PY_VALUE_OPTS = {"-W", "-X", "--check-hash-based-pycs"}


def _py_payload(args):
    """What a python invocation actually runs, from its own parsed args:
    ('code', <-c payload>), ('module', <-m name>), ('script', path), or None
    (REPL / stdin / unparseable). Attached forms (`-cCODE`, `-mmod`) included;
    the scan stops at the first script path because later args belong to the script."""
    i, n = 0, len(args)
    while i < n:
        a = args[i]
        if a in _PY_VALUE_OPTS:
            i += 2
            continue
        if a == "-c" or a == "-m":
            kind = "code" if a == "-c" else "module"
            return (kind, args[i + 1]) if i + 1 < n else None
        if len(a) > 2 and not a.startswith("--") and a[1] in ("c", "m"):
            return ("code" if a[1] == "c" else "module", a[2:])
        if a == "-" or not a.startswith("-"):
            return ("script", a)
        i += 1                          # value-less flag (-u, -B, py's -3, …)
    return None


def _code_is_syntax_check(code: str) -> bool:
    """True if `code` (a `python -c` payload) is a hand-rolled syntax/parse check.
    Detected by parsing the code with Python's own ast module and looking for CALLS to
    ast.parse / py_compile.compile (under any import alias) or the compile() builtin —
    so `from ast import parse as p; p(src)` is caught, while a string literal that
    merely CONTAINS "ast.parse(" is not. Unparseable code falls back to the old
    substring heuristics (it can't run anyway, but stay conservative)."""
    import ast as _ast
    try:
        tree = _ast.parse(code)
    except SyntaxError:
        return bool(re.search(r"\bast\.parse\(|\bpy_compile\b|(?<![\w.])compile\(", code))
    # Names that resolve to the checking callables. Bare module names are pre-seeded so
    # `py_compile.compile(x)` flags even when the import is elsewhere/implicit.
    modules = {"ast": "ast", "py_compile": "py_compile"}   # alias -> module
    checkers = set()                                       # from-imported callables
    for node in _ast.walk(tree):
        if isinstance(node, _ast.Import):
            for a in node.names:
                if a.name in ("ast", "py_compile"):
                    modules[a.asname or a.name] = a.name
        elif isinstance(node, _ast.ImportFrom):
            if node.module in ("ast", "py_compile"):
                for a in node.names:
                    if (node.module, a.name) in (("ast", "parse"), ("py_compile", "compile")):
                        checkers.add(a.asname or a.name)
    for node in _ast.walk(tree):
        if not isinstance(node, _ast.Call):
            continue
        f = node.func
        if isinstance(f, _ast.Name) and (f.id in checkers or f.id == "compile"):
            return True
        if isinstance(f, _ast.Attribute) and isinstance(f.value, _ast.Name):
            mod = modules.get(f.value.id)
            if (mod == "ast" and f.attr == "parse") or (mod == "py_compile" and f.attr == "compile"):
                return True
    return False


def rule_python_validate(cmd: str, commands=None):
    """Steer hand-rolled Python syntax checks to the pre-approved `python -m py_compile`.

    `commands` is the bash_ast per-invocation view when a real parse was available; the
    check then inspects each python invocation's OWN `-c` payload / `-m` module, so text
    in unrelated arguments can't trigger it. Heredoc bodies aren't argument words, so a
    `python - <<EOF` check still goes through the whole-string fallback below."""
    if commands is not None:
        for c in commands:
            if c.leader not in _PY_LEAD:
                continue
            payload = _py_payload(c.args)
            if not payload:
                continue
            kind, val = payload
            if kind == "code" and val and _code_is_syntax_check(val):
                return _PY_VALIDATE_MSG
            if kind == "module" and val == "ast":      # `python -m ast f.py` = parse check
                return _PY_VALIDATE_MSG
        if "<<" not in cmd:
            return None
        # fall through: heredoc body may hold the check
    if leading_command(cmd) not in _PY_LEAD:
        return None
    if re.search(r"-m\s+py_compile\b", cmd):
        return None  # the whitelisted form — leave it alone
    if (re.search(r"\bast\.parse\(", cmd)
            or re.search(r"\bimport\s+py_compile\b", cmd)
            or re.search(r"\bpy_compile\.compile\(", cmd)):
        return _PY_VALIDATE_MSG
    return None


BASH_RULES = (rule_python_validate,)


# git subcommands that publish commits to a remote. These must PROMPT even though
# `Bash(git:*)` is allow-listed (every other git command is auto-accepted).
_REMOTE_PUSH_SUBCMDS = {"push", "send-pack"}


def rule_intended_prompt(seg: str, command=None):
    """Layer-2 whitelist: a single-command segment we've AGREED should reach the user as
    a normal prompt rather than be force-allowed or thrown back. Return a short reason
    string to prompt, or None. Checked PER SEGMENT in evaluate(), before the force-allow
    pass, so an allow-listed-but-sensitive command (a remote push under `git:*`) still
    prompts.

    `command` is the bash_ast.Command (leader + own args) for this segment when a real
    parse was available; the find-mutating check then inspects THIS invocation's args
    (precise). When it's None (parse failed), that check falls back to the whole-string
    token scan on `seg`.

    Add a branch as cases come up that genuinely warrant the user's approval.
    """
    if _git_subcommand(seg) in _REMOTE_PUSH_SUBCMDS:
        return ("A push to a remote publishes your commits and needs your explicit "
                "approval — every other git command is auto-accepted, just this one "
                "prompts. Approve to proceed.")
    # An otherwise read-only tool used in its mutating mode (`find … -delete`/`-exec`,
    # `sed -i`) must prompt even though the bare tool is read-only-fast-allowed or
    # allow-listed — the mutating flag is the rare, dangerous case we want surfaced
    # rather than silently allowed.
    if command is not None:
        trig = file_write_detect.command_conditional_trigger(command.leader, command.args)
    else:
        hits = file_write_detect.conditional_write_triggers(seg)
        trig = hits[0] if hits else None
    if trig:
        base, flag = trig
        return (f"`{base} … {flag}` modifies the filesystem — `{base}` is auto-accepted "
                f"for read-only use, but the `{flag}` action mutates files and needs "
                f"your approval. Approve to proceed.")
    return None


# ---------------------------------------------------------------------------------
# Layer 2: permission allow-list matching (so already-approved commands pass silently).
# ---------------------------------------------------------------------------------

def _project_dir() -> str:
    return os.environ.get("CLAUDE_PROJECT_DIR") or os.getcwd()


def _load_rules(section: str):
    """Merge permissions.<section> (allow/deny) from the project's settings.json +
    settings.local.json. Returns a list of (tool, spec); spec is None for a whole-tool
    rule."""
    rules = []
    base = os.path.join(_project_dir(), ".claude")
    for name in ("settings.json", "settings.local.json"):
        try:
            with open(os.path.join(base, name), encoding="utf-8") as f:
                data = json.load(f)
            for entry in (data.get("permissions") or {}).get(section) or []:
                if not isinstance(entry, str):
                    continue
                m = re.match(r"^([A-Za-z_][A-Za-z0-9_]*)\((.*)\)$", entry, re.DOTALL)
                if m:
                    rules.append((m.group(1), m.group(2)))
                else:
                    rules.append((entry, None))
        except Exception:
            pass
    return rules


def _load_allow_rules():
    return _load_rules("allow")


# Shell constructs we can't safely tokenise into independent sub-commands. If a command
# contains any of these, the hook won't force-allow it — it defers to Claude's own
# permission system (which handles them conservatively) instead of risking a bad allow.
#   $( ) / ` `  -> command substitution     <( ) / >( ) -> process substitution
#   <<          -> heredoc (body would be mis-split on newlines)
_UNSAFE = ("$(", "`", "<(", ">(", "<<")


def _has_unsafe(cmd: str) -> bool:
    """FALLBACK substring scan for when bashlex can't parse (evaluate prefers
    bash_ast.analyze()'s has_substitution/has_heredoc). Coarser: it flags a `$(` even
    inside single quotes, where bashlex correctly sees a literal string."""
    return any(tok in cmd for tok in _UNSAFE)


# Redirections that discard output to the null device are harmless (they write
# nothing) — strip them before the scan so `cmd 2>/dev/null` isn't treated as a file
# write. Covers an optional fd or `&` prefix, `>`/`>>`, optional spaces, and /dev/null
# or Windows nul. Kept deliberately simple per the "basics with regex" intent.
_NULL_REDIRECT = re.compile(
    r"(?:\d*|&)>>?\s*(?:/dev/null|nul)\b"   # discard to null device: 2>/dev/null, &>nul
    r"|\d*>>?&\d+",                          # fd duplication: 2>&1, >&2 (not a file write)
    re.IGNORECASE)


def has_output_redirection(command: str) -> bool:
    """FALLBACK for when bashlex can't parse (evaluate prefers bash_ast.analyze().writes_file).

    Checks if a bash command string redirects output to a file using '>' or
    '>>'. Safely ignores redirection symbols inside single quotes, double quotes,
    and escaped characters. Catches '>', '>>', '2>', '&>', etc. by simply looking
    for an unquoted '>'. (Note: also returns True for unquoted bash arithmetic
    like `$(( 5 > 3 ))`, but those are already caught by _has_unsafe's '$('.)

    Redirections to the null device (`2>/dev/null`, `>/dev/null`, `&>nul`, …) are
    stripped first: they discard output rather than write a file, so they shouldn't
    force a prompt."""
    command = _NULL_REDIRECT.sub("", command)
    in_single_quote = False
    in_double_quote = False
    escape_next = False

    for char in command:
        if escape_next:
            escape_next = False
            continue

        if char == '\\':
            # Backslash is literal inside single quotes; an escape otherwise.
            if not in_single_quote:
                escape_next = True
            continue

        if char == "'" and not in_double_quote:
            in_single_quote = not in_single_quote
            continue

        if char == '"' and not in_single_quote:
            in_double_quote = not in_double_quote
            continue

        if not in_single_quote and not in_double_quote:
            if char == '>':
                return True

    return False


def _split_segments(cmd: str):
    """FALLBACK for when bashlex can't parse (evaluate prefers bash_ast.analyze().segments).

    Split a Bash command into independent sub-commands on the shell operators
    && || ; | and newlines, respecting single/double quotes. Used only after _has_unsafe
    has ruled out substitutions/heredocs, so a naive quote-aware scan is sufficient."""
    segs, buf, quote, i, n = [], "", None, 0, len(cmd)
    while i < n:
        c = cmd[i]
        if quote:
            buf += c
            if c == quote:
                quote = None
            i += 1
            continue
        if c in ('"', "'"):
            quote = c
            buf += c
            i += 1
            continue
        if cmd[i:i + 2] in ("&&", "||"):
            segs.append(buf)
            buf = ""
            i += 2
            continue
        if c in (";", "|", "\n"):
            segs.append(buf)
            buf = ""
            i += 1
            continue
        buf += c
        i += 1
    segs.append(buf)
    return [s.strip() for s in segs if s.strip()]


def _tokenize(cmd: str):
    """Quote-aware split of a single command into argv-style tokens, with surrounding
    quotes stripped. `robocopy "G:\\a b" "C:\\c" /E` -> ['robocopy', 'G:\\a b', 'C:\\c',
    '/E']. Good enough for inspecting a robocopy invocation's positionals."""
    toks, buf, quote, started = [], "", None, False
    for c in cmd:
        if quote:
            if c == quote:
                quote = None
            else:
                buf += c
            continue
        if c in ('"', "'"):
            quote, started = c, True
            continue
        if c.isspace():
            if started:
                toks.append(buf)
                buf, started = "", False
            continue
        buf += c
        started = True
    if started:
        toks.append(buf)
    return toks


# git global options that consume the FOLLOWING token as their value, so the
# subcommand parser knows to skip two tokens, not one (e.g. `git -C path push`).
_GIT_VALUE_OPTS = {"-C", "-c", "--git-dir", "--work-tree", "--namespace",
                   "--exec-path", "--config-env"}


def _git_subcommand(cmd: str):
    """Return the git subcommand of a single command segment (lowercased), or None if
    the segment isn't a git invocation. Skips leading `VAR=val` env prefixes and git
    global options so `git -c k=v push` -> 'push' and `git log --grep=push` -> 'log'."""
    toks = _tokenize(cmd)
    i = 0
    while i < len(toks) and re.match(r"^[A-Za-z_][A-Za-z0-9_]*=", toks[i]):
        i += 1
    if i >= len(toks):
        return None
    base = toks[i].rsplit("/", 1)[-1].rsplit("\\", 1)[-1].lower()
    if base not in ("git", "git.exe"):
        return None
    i += 1
    while i < len(toks):
        t = toks[i]
        if t.startswith("-"):
            i += 2 if t in _GIT_VALUE_OPTS else 1
            continue
        return t.lower()
    return None


def rule_robocopy_into_project(cmd: str, project_dir=None) -> bool:
    """True if `cmd` is a `robocopy SOURCE DEST [files…] [/opts]` whose DEST resolves
    inside the project directory. Source may be anywhere. A destructive mirror/purge
    (/MIR, /PURGE) is excluded so it still prompts. Used as an extra per-segment allow
    predicate, so the user isn't prompted to copy files into their own project."""
    toks = _tokenize(cmd)
    # Skip any leading `VAR=value` env-assignment prefixes (e.g. the
    # `MSYS_NO_PATHCONV=1 robocopy …` needed so Git Bash doesn't mangle /-flags).
    idx = 0
    while idx < len(toks) and re.match(r"^[A-Za-z_][A-Za-z0-9_]*=", toks[idx]):
        idx += 1
    if idx >= len(toks):
        return False
    base = toks[idx].rsplit("/", 1)[-1].rsplit("\\", 1)[-1].lower()
    if base not in ("robocopy", "robocopy.exe"):
        return False
    args = toks[idx + 1:]
    if any(a.lower() in ("/mir", "/purge") for a in args):
        return False
    positionals = [a for a in args if not a.startswith("/")]
    if len(positionals) < 2:
        return False
    dest = positionals[1]
    pd = project_dir or _project_dir()
    if not os.path.isabs(dest):
        dest = os.path.join(pd, dest)
    dest_abs = os.path.normcase(os.path.abspath(dest))
    proj_abs = os.path.normcase(os.path.abspath(pd))
    return dest_abs == proj_abs or dest_abs.startswith(proj_abs + os.sep)


# mkdir options that consume the FOLLOWING token as their value (the mode).
_MKDIR_VALUE_OPTS = {"-m", "--mode"}


def rule_mkdir_into_project(cmd: str, project_dir=None) -> bool:
    """True if `cmd` is a `mkdir [-p] [-m MODE] DIR…` whose EVERY target directory
    resolves inside the project directory. Used as an extra per-segment allow predicate
    (alongside rule_robocopy_into_project) so the user isn't prompted to create
    directories inside their own project. A mkdir whose target is outside the project —
    or any unparseable form — returns False and falls through to the normal flow, so
    other directories pass through as usual."""
    toks = _tokenize(cmd)
    # Skip any leading `VAR=value` env-assignment prefixes.
    idx = 0
    while idx < len(toks) and re.match(r"^[A-Za-z_][A-Za-z0-9_]*=", toks[idx]):
        idx += 1
    if idx >= len(toks):
        return False
    base = toks[idx].rsplit("/", 1)[-1].rsplit("\\", 1)[-1].lower()
    if base not in ("mkdir", "mkdir.exe"):
        return False
    args = toks[idx + 1:]
    targets, i = [], 0
    while i < len(args):
        a = args[i]
        if a in _MKDIR_VALUE_OPTS:   # `-m 755` — the mode is a separate token, skip both
            i += 2
            continue
        if a.startswith("-"):        # `-p`, `-v`, `-m755`, `--mode=755` — no separate token
            i += 1
            continue
        targets.append(a)
        i += 1
    if not targets:
        return False
    pd = project_dir or _project_dir()
    proj_abs = os.path.normcase(os.path.abspath(pd))
    for dest in targets:
        d = dest if os.path.isabs(dest) else os.path.join(pd, dest)
        d_abs = os.path.normcase(os.path.abspath(d))
        if not (d_abs == proj_abs or d_abs.startswith(proj_abs + os.sep)):
            return False
    return True


def _spec_matches(spec: str, subject: str) -> bool:
    glob = spec[:-2] + "*" if spec.endswith(":*") else spec   # `git:*` -> `git*`
    if fnmatch.fnmatch(subject, glob):
        return True
    prefix = spec[:-2] if spec.endswith(":*") else spec.rstrip("*")
    return bool(prefix) and subject.strip().startswith(prefix.strip())


def _allow_listed(tool_name: str, subject: str, rules) -> bool:
    for rt, spec in rules:
        if rt != tool_name:
            continue
        if spec is None or _spec_matches(spec, subject):
            return True
    return False


GENERIC_DENY = (
    "This Bash command isn't on the no-prompt allow-list, so it would ask the user to "
    "approve it. The hook blocks Bash prompts by default (deny-by-default; see "
    "docs/avoid-prompting-user.md). Prefer a dedicated tool or an allow-listed command "
    "for this job."
)

# ---------------------------------------------------------------------------------
# Decision core (pure, unit-testable).
# ---------------------------------------------------------------------------------

def evaluate(tool_name: str, tool_input: dict, mode: str, allow_rules, deny_rules=(),
             project_dir=None):
    """Pre-resend verdict. Returns (kind, reason):
       kind 'allow' -> force-allow; suppress the prompt (every sub-command is allow-listed)
       kind 'defer' -> emit nothing; let the normal permission flow decide
       kind 'ask'   -> prompt the user now (intended prompt)
       kind 'deny'  -> throw back (reason set)
    """
    # Explicit modes own their own gating; only Bash is governed.
    if mode in ("bypassPermissions", "plan") or tool_name not in DENY_DEFAULT_TOOLS:
        return ("defer", None)

    cmd = str(tool_input.get("command", ""))

    pd = project_dir or _project_dir()

    # bashlex-backed structural read of the command. When it parses (.ok), its segment
    # split, substitution/heredoc detection, and real-file-write detection are exact and
    # supersede the quote-aware regex heuristics below. When it can't parse (arithmetic
    # expansion, syntax errors), .ok is False and we fall back to those heuristics — the
    # deny-by-default posture means a failed parse degrades to the old behaviour, never to
    # a wrong allow. See bash_ast.py.
    ast = bash_ast.analyze(cmd)

    # Layer 1: special-case redirects (fire even when allow-listed) — e.g. a hand-rolled
    # python syntax check is steered to `python -m py_compile`. Kept ahead of the
    # read-only fast-allow below so these redirects win over a plain allow. Rules get the
    # parsed per-invocation view when available (None on parse failure -> they fall back
    # to their whole-string heuristics).
    for rule in BASH_RULES:
        reason = rule(cmd, ast.commands if ast.ok else None)
        if reason:
            return ("deny", reason)

    # Primary path is the AST: segments/commands are index-aligned, so command[i] is the
    # parsed (leader, args) view of segment[i]. When bashlex can't parse (arithmetic
    # expansion, syntax errors), commands is None and every downstream check falls back to
    # the whole-string heuristics — deny-by-default means that degrades safely.
    if ast.ok:
        segments, commands = ast.segments, ast.commands
    else:
        segments, commands = _split_segments(cmd), None

    # Sensitive-but-allow-listed commands (a remote push under `git:*`, `find … -delete`)
    # must PROMPT even though they'd otherwise be force-allowed. Check per segment BEFORE
    # force-allow — and before the unsafe check, so `git push $(…)` can't slip through on a
    # defer to git:*. Pass the aligned parsed command so the find-mutating check inspects
    # this invocation's own args rather than the whole string.
    for i, seg in enumerate(segments or [cmd]):
        command = commands[i] if commands is not None and i < len(commands) else None
        intended = rule_intended_prompt(seg, command)
        if intended:
            return ("ask", intended)

    if ast.ok:
        unsafe = ast.has_substitution or ast.has_heredoc
        file_redirection = ast.writes_file
        # Per-invocation mutation check: a write-named token is a mutator only when it's the
        # LEADER of a command, not when it rides in another command's args (`echo rm`,
        # `grep -n kill .`). Redirection is covered separately by ast.writes_file above.
        side_effect = any(file_write_detect.command_side_effect(c.leader, c.args)
                          for c in commands)
    else:
        unsafe = _has_unsafe(cmd)
        file_redirection = has_output_redirection(cmd)
        side_effect = file_write_detect.has_side_effects(cmd)

    # Read-only fast-allow: a command with no side effects (no file-writing/process/power
    # command, no redirection) and no unparseable substitution/heredoc can't do the damage
    # a prompt guards against — accept it as is. This is the positive-safety inverse of the
    # allow-list: instead of enumerating safe commands, file_write_detect enumerates the
    # ways to change state and we allow the rest. Conservative by construction
    # (interpreters/wrappers/network tools all count), so a False is a strong read-only signal.
    if not unsafe and not file_redirection and not side_effect:
        return ("allow", None)

    # Never force-allow a command that redirects output to a file (`>`/`>>`/`2>`/…).
    # An allow rule like `echo *` would otherwise silently sanction a file write.
    if unsafe or file_redirection:
        return ("defer", None)

    # Layer 2: if EVERY sub-command of the (possibly compound) command is allow-listed,
    # force-allow so the user isn't prompted — Claude's own permission system otherwise
    # prompts on `;`-chained compounds even when each part is individually allowed. Never
    # force-allow if any segment hits a `deny` rule (respect the user's deny-list).
    def _seg_ok(s):
        return (_allow_listed("Bash", s, allow_rules)
                or rule_robocopy_into_project(s, pd)
                or rule_mkdir_into_project(s, pd))

    if segments and not any(_allow_listed("Bash", s, deny_rules) for s in segments):
        if all(_seg_ok(s) for s in segments):
            return ("allow", None)

    # Default: throw it back.
    return ("deny", GENERIC_DENY)


# ---------------------------------------------------------------------------------
# State store (per session) for the immediate-resend bypass.
# ---------------------------------------------------------------------------------

def _state_path(session_id: str) -> str:
    key = session_id or hashlib.sha1(_project_dir().encode()).hexdigest()[:12]
    safe = re.sub(r"[^A-Za-z0-9_.-]", "_", key)[:64]
    return os.path.join(tempfile.gettempdir(), f"bash_guard_last_denied_{safe}.txt")


def _read(path: str) -> str:
    try:
        with open(path, encoding="utf-8") as f:
            return f.read()
    except Exception:
        return ""


def _write(path: str, s: str) -> None:
    try:
        with open(path, "w", encoding="utf-8") as f:
            f.write(s)
    except Exception:
        pass


def _clear(path: str) -> None:
    try:
        os.remove(path)
    except Exception:
        pass


def _emit(decision: str, reason):
    out = {"hookSpecificOutput": {
        "hookEventName": "PreToolUse",
        "permissionDecision": decision,          # 'allow' | 'deny' | 'ask'
    }}
    if reason is not None:
        out["hookSpecificOutput"]["permissionDecisionReason"] = reason
    print(json.dumps(out))


def main() -> None:
    try:
        data = json.load(sys.stdin)
    except Exception:
        sys.exit(0)  # unparseable input -> don't block

    tool_name = data.get("tool_name") or ""
    # Fast path: tools this hook doesn't govern are left entirely alone.
    if tool_name not in DENY_DEFAULT_TOOLS:
        sys.exit(0)

    tool_input = data.get("tool_input")
    if not isinstance(tool_input, dict):
        tool_input = {}
    mode = data.get("permission_mode") or ""

    kind, reason = evaluate(tool_name, tool_input, mode,
                            _load_allow_rules(), _load_rules("deny"))

    state = _state_path(str(data.get("session_id") or ""))

    if kind == "deny":
        last = _read(state)
        ident = str(tool_input.get("command", "")).strip()
        if last.strip() and ident == last.strip():
            # Immediate, deliberate re-send -> let the user approve, clear the store.
            _clear(state)
            _emit("ask", "Re-sent unchanged after a redirect — asking you to approve.")
        else:
            _write(state, ident)
            _emit("deny", reason + RESEND_HINT)
        sys.exit(0)

    # Any non-deny verdict clears the store (so a later accidental repeat is fresh).
    _clear(state)
    if kind == "allow":
        _emit("allow", None)
    elif kind == "ask":
        _emit("ask", reason)
    # kind == "defer": print nothing, defer to the normal permission flow.
    sys.exit(0)


if __name__ == "__main__":
    main()
