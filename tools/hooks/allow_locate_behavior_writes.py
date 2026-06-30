#!/usr/bin/env python3
"""PreToolUse hook: auto-approve Write/Edit/MultiEdit to curated, git-tracked .claude config.

Force-ALLOWS (promptless) edits to config we touch routinely:
  - `.claude/skills/`  (incl. the locate-behavior self-healing map the finder writes)
  - `.claude/agents/`  (curated subagent definitions)
acceptEdits carves `.claude/` config out (so it would otherwise prompt), and relative globs
in settings.json don't match the absolute backslash paths Claude resolves on Windows, so an
explicit hook "allow" is the only reliable way to make these frictionless.

EXCEPTION — a `settings*.json` nested anywhere under those folders is NOT force-allowed; it
defers to the normal flow so it prompts. Permission config must be reviewed, never auto-edited.

Everything else defers (no decision). The "must review" paths that live OUTSIDE `.claude/`
(notably `tools/hooks/`) are gated by `ask` RULES in settings.json, not here — `ask` rules,
unlike a hook "ask", present (and honor) the dialog's "Always allow (this session)" button,
which a hook-forced prompt cannot. So we deliberately leave that to the permission system.
"""
import json
import sys

# Tools whose target file we gate on. MultiEdit/Edit/Write all carry file_path.
_FILE_TOOLS = {"Write", "Edit", "MultiEdit"}
# Forward-slash, lowercased markers; path contains one -> auto-approve (unless it's a settings file).
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
        return 0  # outside curated folders -> normal flow (acceptEdits / ask rules / dialog)

    # Never auto-approve a settings file, even nested in a curated folder -> let it prompt.
    base = norm.rsplit("/", 1)[-1]
    if base.startswith("settings") and base.endswith(".json"):
        return 0

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
