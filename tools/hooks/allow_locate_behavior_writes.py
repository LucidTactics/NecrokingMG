#!/usr/bin/env python3
"""PreToolUse hook: auto-approve Write/Edit/MultiEdit to curated, git-tracked .claude config.

Covers two folders of shared, version-controlled config that we edit routinely:
  - `.claude/skills/locate-behavior/` — the self-healing architecture map the
    locate-behavior-finder subagent writes (overview.md + reference/<area>.md).
  - `.claude/agents/` — curated subagent definitions (e.g. locate-behavior-finder.md).

These writes are meant to be promptless, but relative forward-slash path globs in
settings.json don't match the absolute backslash paths Claude resolves on Windows, so the
allow rule never fires and every write prompts. This hook normalizes the path and approves
the write when it lands inside one of those folders — portable across machines/OSes (no
hard-coded home dir), unlike an absolute glob.

Deliberately NOT covered: `.claude/settings*.json` and other `.claude/` files — those
govern permissions/hooks themselves, so they stay on the normal permission flow. Anything
outside the listed folders is left to the user (we stay silent).
"""
import json
import sys

# Tools whose target file we gate on. MultiEdit/Edit/Write all carry file_path.
_FILE_TOOLS = {"Write", "Edit", "MultiEdit"}
# Forward-slash, lowercased markers; the resolved path must contain one to be auto-approved.
_ALLOWED_MARKERS = (
    "/.claude/skills/locate-behavior/",
    "/.claude/agents/",
)


def main() -> int:
    try:
        payload = json.load(sys.stdin)
    except Exception:
        return 0  # malformed input -> defer to normal flow

    if payload.get("tool_name") not in _FILE_TOOLS:
        return 0

    path = (payload.get("tool_input") or {}).get("file_path", "")
    if not path:
        return 0

    norm = path.replace("\\", "/").lower()
    if not any(marker in norm for marker in _ALLOWED_MARKERS):
        return 0  # outside the curated folders -> let the user decide

    print(json.dumps({
        "hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": "allow",
            "permissionDecisionReason": "curated .claude config (skills/locate-behavior or agents)",
        }
    }))
    return 0


if __name__ == "__main__":
    sys.exit(main())
