#!/usr/bin/env python3
"""PreToolUse hook: auto-approve Write/Edit/MultiEdit to curated, git-tracked .claude config.

Covers two folders of shared, version-controlled config that we edit routinely:
  - `.claude/skills/` — all skill files, incl. the locate-behavior self-healing
    architecture map (overview.md + reference/<area>.md) the finder subagent writes.
  - `.claude/agents/` — curated subagent definitions (e.g. locate-behavior-finder.md).

These writes are meant to be promptless, but relative forward-slash path globs in
settings.json don't match the absolute backslash paths Claude resolves on Windows, so the
allow rule never fires and every write prompts. This hook normalizes the path and approves
the write when it lands inside one of those folders — portable across machines/OSes (no
hard-coded home dir), unlike an absolute glob.

Deliberately NOT covered: any `settings*.json` (matched by basename, so even one nested
inside a curated folder like `.claude/skills/x/settings.json`) and any `.claude/` file
outside the two folders above. Permission/hook config must always prompt — editing it is
an escalation, not a self-heal. Anything outside the listed folders is left to the user
(we stay silent).
"""
import json
import sys

# Tools whose target file we gate on. MultiEdit/Edit/Write all carry file_path.
_FILE_TOOLS = {"Write", "Edit", "MultiEdit"}
# Forward-slash, lowercased markers; the resolved path must contain one to be auto-approved.
_ALLOWED_MARKERS = (
    "/.claude/skills/",
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

    # NEVER auto-approve a settings file, even nested inside a curated folder. Permission/
    # hook config must always prompt — a self-edit there is an escalation, not a self-heal.
    base = norm.rsplit("/", 1)[-1]
    if base.startswith("settings") and base.endswith(".json"):
        return 0  # e.g. .../skills/x/settings.json -> defer to normal (prompting) flow

    print(json.dumps({
        "hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": "allow",
            "permissionDecisionReason": "curated .claude config (skills/agents, non-settings)",
        }
    }))
    return 0


if __name__ == "__main__":
    sys.exit(main())
