#!/usr/bin/env python3
"""PreToolUse hook: auto-approve Write/Edit/MultiEdit to the locate-behavior skill folder.

The locate-behavior-finder subagent self-heals the architecture map by writing/editing
docs under `.claude/skills/locate-behavior/` (overview.md + reference/<area>.md). Those
writes are intended to be promptless, but relative forward-slash path globs in
settings.json don't match the absolute backslash paths Claude resolves on Windows, so
the allow rule never fires and every doc write prompts.

This hook normalizes the path and approves the write when it lands inside the skill
folder — portable across machines/OSes (no hard-coded home dir), unlike an absolute glob.
Anything outside that folder is left to the normal permission flow (we stay silent).
"""
import json
import sys

# Tools whose target file we gate on. MultiEdit/Edit/Write all carry file_path.
_FILE_TOOLS = {"Write", "Edit", "MultiEdit"}
# Forward-slash, lowercased marker the resolved path must contain to be auto-approved.
_ALLOWED_MARKER = "/.claude/skills/locate-behavior/"


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
    if _ALLOWED_MARKER not in norm:
        return 0  # outside the skill folder -> let the user decide

    print(json.dumps({
        "hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": "allow",
            "permissionDecisionReason": "locate-behavior map self-heal (skill folder write)",
        }
    }))
    return 0


if __name__ == "__main__":
    sys.exit(main())
