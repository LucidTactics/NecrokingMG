#!/usr/bin/env python3
"""PreToolUse hook (Bash): redirect standalone read-only search/inspect commands to
the dedicated Grep / Glob / Read tools.

Denies ONLY when the *leading* command (optionally after one or more `cd ... &&`
prefixes) is one of grep/rg/find/cat/head/tail. Legit pipeline uses where the command
appears later — `dotnet build | grep error`, `git log | cat` — are left alone, since
those can't be done by the dedicated tools.

Output contract: print a PreToolUse deny JSON and exit 0 to block; print nothing and
exit 0 to allow. Any parse error → allow (never wedge the Bash tool on a hook bug).
"""
import json
import re
import sys

BLOCKED = {"grep", "rg", "find", "cat", "head", "tail"}


def leading_command(cmd: str) -> str:
    s = cmd.strip()
    # Peel any leading `cd <path> &&` prefixes (e.g. `cd repo && grep ...`).
    while True:
        m = re.match(r"^cd\s+[^&|;]+&&\s*", s)
        if not m:
            break
        s = s[m.end():]
    # Skip a leading subshell / grouping opener so `( grep ... )` is still caught.
    s = s.lstrip("({ \t")
    # First bare token (stop at whitespace or a shell operator).
    m = re.match(r"[^\s|;&<>()]+", s)
    if not m:
        return ""
    tok = m.group(0)
    # Reduce a path to its basename so /usr/bin/grep still matches.
    return tok.rsplit("/", 1)[-1].rsplit("\\", 1)[-1]


def main() -> None:
    try:
        data = json.load(sys.stdin)
    except Exception:
        sys.exit(0)  # unparseable input → don't block

    cmd = (data.get("tool_input") or {}).get("command", "")
    if not isinstance(cmd, str) or not cmd.strip():
        sys.exit(0)

    lead = leading_command(cmd)
    if lead in BLOCKED:
        reason = (
            f"Use the dedicated Grep (content search), Glob (file patterns), or Read "
            f"(cat/head/tail) tools instead of running '{lead}' via Bash — they integrate "
            f"with the permission UI, return clickable file links, and are faster. If you "
            f"genuinely need a shell pipeline, re-run with the search command piped (not "
            f"leading), e.g. `<cmd> | {lead} ...`."
        )
        print(json.dumps({
            "hookSpecificOutput": {
                "hookEventName": "PreToolUse",
                "permissionDecision": "deny",
                "permissionDecisionReason": reason,
            }
        }))
    sys.exit(0)


if __name__ == "__main__":
    main()
