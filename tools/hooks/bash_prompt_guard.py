#!/usr/bin/env python3
"""PreToolUse hook (Bash): redirect commands to whitelisted, no-prompt alternatives so
the user isn't asked to approve things that shouldn't need it.

Posture: ERR ON DENYING. Every deny ends with an escape hatch — re-sending the EXACT
same command immediately lets it through to a normal user prompt. Because that hatch
always exists, a rule can be broad/aggressive without ever permanently blocking a
genuinely-needed command. See docs/avoid-prompting-user.md.

Re-send bypass (state machine):
  * The last denied command is stored (per session, in the OS temp dir).
  * If the very next Bash command is byte-identical to it, the hook emits `ask` so the
    user is prompted, and clears the stored command.
  * ANY other command — one that's allowed, OR a different command that's denied —
    clears/overwrites the stored command. So an accidental later repeat of a denied
    command does NOT silently slip through; only an *immediate* deliberate re-send does.

Output: print a deny/ask JSON to act; print nothing to allow. Any error -> allow (never
wedge the Bash tool on a hook bug).
"""
import hashlib
import json
import os
import re
import sys
import tempfile

RESEND_HINT = ("\n\nIf you really cannot use another method, send the exact same command "
               "again and it will prompt the user.")


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


RULES = (rule_search, rule_python_validate)


def decide(cmd: str, last: str):
    """Pure decision core (no IO, so it's unit-testable).

    Returns (action, new_last, reason):
      action   : 'allow' | 'deny' | 'ask'
      new_last : the command to remember as "last denied" ("" = clear the store)
      reason   : message for deny/ask (None for allow)
    """
    reason = None
    for rule in RULES:
        reason = rule(cmd)
        if reason:
            break

    if reason is None:
        # Allowed → forget any pending denied command (a stale match must never slip a
        # later accidental repeat through).
        return ("allow", "", None)

    if last.strip() and cmd.strip() == last.strip():
        # Immediate, deliberate re-send of the just-denied command → let the user
        # approve it, and clear the store.
        return ("ask", "", "Re-sent unchanged after a redirect — asking you to approve.")

    # First time we've seen this denied command → remember it and deny with the hatch.
    return ("deny", cmd.strip(), reason + RESEND_HINT)


def _state_path(session_id: str) -> str:
    key = session_id or hashlib.sha1(
        (os.environ.get("CLAUDE_PROJECT_DIR") or os.getcwd()).encode()).hexdigest()[:12]
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


def main() -> None:
    try:
        data = json.load(sys.stdin)
    except Exception:
        sys.exit(0)  # unparseable input -> don't block

    cmd = (data.get("tool_input") or {}).get("command", "")
    if not isinstance(cmd, str) or not cmd.strip():
        sys.exit(0)

    state = _state_path(str(data.get("session_id") or ""))
    action, new_last, reason = decide(cmd, _read(state))

    # Persist the decision's view of the store FIRST, so it's correct even if the
    # command is later denied at the prompt.
    if new_last:
        _write(state, new_last)
    else:
        _clear(state)

    if action == "allow":
        sys.exit(0)

    print(json.dumps({
        "hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": action,  # 'deny' or 'ask'
            "permissionDecisionReason": reason,
        }
    }))
    sys.exit(0)


if __name__ == "__main__":
    main()
