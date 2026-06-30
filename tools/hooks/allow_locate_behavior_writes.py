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

Always FORCES A PROMPT (permissionDecision "ask", which overrides acceptEdits) for the
permission-granting machinery, so it can never be edited on autopilot:
  - any `settings*.json` (matched by basename, even nested like `.claude/skills/x/settings.json`),
  - anything under `tools/hooks/` (the hook scripts themselves — including THIS file).
We use "ask" rather than just staying silent because hook scripts live outside `.claude/`,
so acceptEdits would otherwise auto-accept them. Editing permission/hook config is an
escalation, not a self-heal. Everything else outside the curated folders is left to the
user (we stay silent / defer to normal flow).
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
# Path fragments that must ALWAYS prompt, even under acceptEdits / inside a curated folder.
_ALWAYS_ASK_MARKERS = (
    "/tools/hooks/",
)


def _emit(decision: str, reason: str) -> int:
    print(json.dumps({
        "hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": decision,
            "permissionDecisionReason": reason,
        }
    }))
    return 0


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
    base = norm.rsplit("/", 1)[-1]

    # Guardrail first: permission/hook config always prompts (overrides acceptEdits).
    if base.startswith("settings") and base.endswith(".json"):
        return _emit("ask", "settings file — permission config must be reviewed")
    if any(marker in norm for marker in _ALWAYS_ASK_MARKERS):
        return _emit("ask", "hook script — permission machinery must be reviewed")

    # Otherwise auto-approve curated, version-controlled skill/agent config.
    if any(marker in norm for marker in _ALLOWED_MARKERS):
        return _emit("allow", "curated .claude config (skills/agents, non-settings)")

    return 0  # outside the curated folders -> let the user decide


if __name__ == "__main__":
    sys.exit(main())
