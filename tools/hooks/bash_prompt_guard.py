#!/usr/bin/env python3
"""PreToolUse hook (Bash): redirect Bash commands to whitelisted, no-prompt
alternatives so the user isn't asked to approve things that shouldn't need it.

See docs/avoid-prompting-user.md. Each rule recognizes a class of command that has a
better, non-prompting path and DENIES it with a message pointing Claude there. Rules
fire ONLY on clear cases — never on legitimate uses the whitelisted method can't cover
(e.g. `grep` inside a pipeline). Print a PreToolUse deny JSON to block; print nothing
to allow. Any error -> allow (never wedge the Bash tool on a hook bug).
"""
import json
import re
import sys


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


def main() -> None:
    try:
        data = json.load(sys.stdin)
    except Exception:
        sys.exit(0)  # unparseable input -> don't block

    cmd = (data.get("tool_input") or {}).get("command", "")
    if not isinstance(cmd, str) or not cmd.strip():
        sys.exit(0)

    for rule in RULES:
        reason = rule(cmd)
        if reason:
            print(json.dumps({
                "hookSpecificOutput": {
                    "hookEventName": "PreToolUse",
                    "permissionDecision": "deny",
                    "permissionDecisionReason": reason,
                }
            }))
            break
    sys.exit(0)


if __name__ == "__main__":
    main()
