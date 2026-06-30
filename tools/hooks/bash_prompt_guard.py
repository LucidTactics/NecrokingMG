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
     alternative for a specific job: filesystem search -> Grep/Glob/Read; hand-rolled
     syntax check -> `python -m py_compile`. These fire even on otherwise allow-listed
     commands (e.g. `cat`), because the dedicated tool is strictly better.

  2. Deny-by-default — any other Bash command is thrown back UNLESS it is:
       * fully allow-listed: every sub-command of the (possibly compound) command matches
         a permission allow rule (.claude/settings*.json). The hook splits on && || ; |
         and newlines and force-ALLOWS the whole thing, so the user isn't prompted —
         Claude's own permission system otherwise prompts on `;`-chained compounds even
         when each part is individually allowed (e.g. `cd x; git status; echo done`).
         Commands containing substitutions/heredocs ($(), ``, <(), <<) are NOT
         force-allowed — they defer to the normal permission flow instead;
       * explicitly whitelisted as "OK to prompt the user" (rule_intended_prompt) -> the
         user is prompted immediately instead of the command being thrown back.
     Everything else -> deny with the escape hatch.

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


# --- Rule: filesystem search / inspection -> Grep / Glob / Read tools -------------
_SEARCH_CMDS = {"grep", "rg", "find", "cat", "head", "tail"}


def rule_search(cmd: str):
    lead = leading_command(cmd)
    if lead in _SEARCH_CMDS:
        return (
            f"Use the dedicated Grep (content search), Glob (file patterns), or Read "
            f"(cat/head/tail) tools instead of running '{lead}' via Bash — they integrate "
            f"with the permission UI, return clickable file links, and are faster. If you "
            f"genuinely need a shell pipeline, re-run with the search command piped (not "
            f"leading), e.g. `<cmd> | {lead} ...`."
        )
    return None


# --- Rule: hand-rolled python syntax check -> `python -m py_compile` --------------
_PY_LEAD = {"python", "python3", "py"}


def rule_python_validate(cmd: str):
    if leading_command(cmd) not in _PY_LEAD:
        return None
    if re.search(r"-m\s+py_compile\b", cmd):
        return None  # the whitelisted form — leave it alone
    if (re.search(r"\bast\.parse\(", cmd)
            or re.search(r"\bimport\s+py_compile\b", cmd)
            or re.search(r"\bpy_compile\.compile\(", cmd)):
        return (
            "Validate Python syntax with `python -m py_compile <file>` — it's pre-approved "
            "in .claude/settings.json, so it runs without prompting the user. Re-run as "
            "`python -m py_compile <file>` instead of a hand-rolled ast.parse / py_compile "
            "check."
        )
    return None


BASH_RULES = (rule_search, rule_python_validate)


# git subcommands that publish commits to a remote. These must PROMPT even though
# `Bash(git:*)` is allow-listed (every other git command is auto-accepted).
_REMOTE_PUSH_SUBCMDS = {"push", "send-pack"}


def rule_intended_prompt(cmd: str):
    """Layer-2 whitelist: a single-command segment we've AGREED should reach the user as
    a normal prompt rather than be force-allowed or thrown back. Return a short reason
    string to prompt, or None. Checked PER SEGMENT in evaluate(), before the force-allow
    pass, so an allow-listed-but-sensitive command (a remote push under `git:*`) still
    prompts.

    Add a branch as cases come up that genuinely warrant the user's approval.
    """
    if _git_subcommand(cmd) in _REMOTE_PUSH_SUBCMDS:
        return ("A push to a remote publishes your commits and needs your explicit "
                "approval — every other git command is auto-accepted, just this one "
                "prompts. Approve to proceed.")
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
    return any(tok in cmd for tok in _UNSAFE)


def _split_segments(cmd: str):
    """Split a Bash command into independent sub-commands on the shell operators
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

    # Layer 1: special-case redirects (fire even when allow-listed).
    for rule in BASH_RULES:
        reason = rule(cmd)
        if reason:
            return ("deny", reason)

    pd = project_dir or _project_dir()
    segments = _split_segments(cmd)

    # Sensitive-but-allow-listed commands (a remote push under `git:*`) must PROMPT even
    # though they'd otherwise be force-allowed. Check per segment BEFORE force-allow — and
    # before _has_unsafe, so `git push $(…)` can't slip through on a defer to git:*.
    for seg in (segments or [cmd]):
        intended = rule_intended_prompt(seg)
        if intended:
            return ("ask", intended)

    # Constructs we can't safely tokenise -> hand off to the normal permission flow.
    if _has_unsafe(cmd):
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
