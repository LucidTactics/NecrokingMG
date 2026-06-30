#!/usr/bin/env python3
"""Pre/PostToolUse hook governing Write/Edit/MultiEdit to curated config + the permission machinery.

Three tiers, by target path:

  ALLOW (promptless) — curated, version-controlled config we edit routinely:
    - `.claude/skills/`  (incl. the locate-behavior self-healing map the finder writes)
    - `.claude/agents/`  (curated subagent definitions)
  These are auto-approved. acceptEdits won't cover them (Claude carves `.claude/` config out
  of acceptEdits), and relative globs in settings.json don't match Windows absolute paths, so
  an explicit hook "allow" is the only clean way to make them frictionless.

  ASK-ONCE-PER-SESSION — the permission machinery itself:
    - any `settings*.json` (matched by basename, even nested under a curated folder)
    - anything under `tools/hooks/` (the hook scripts, incl. THIS file)
  Editing these is an escalation, not a self-heal, so they must be reviewed. But re-prompting
  on every edit is tedious, so we grant per SESSION: the first such edit prompts ("ask",
  which overrides acceptEdits); once the user approves it, a PostToolUse flag is written and
  the rest of that session is auto-allowed for that category. `settings` and `hooks` are
  tracked separately, so approving one does NOT silently open the other.

  DEFER — everything else: no decision, normal flow (acceptEdits handles source files).

Hook decisions run before stored allow rules, so a user-clicked "Always allow" can't suppress
our "ask" — the per-session flag (keyed off the payload's session_id) is what remembers consent.
Register this script under BOTH PreToolUse and PostToolUse for matcher Write|Edit|MultiEdit.
"""
import json
import os
import sys
import tempfile

# Tools whose target file we gate on. MultiEdit/Edit/Write all carry file_path.
_FILE_TOOLS = {"Write", "Edit", "MultiEdit"}
# Forward-slash, lowercased markers; path contains one -> auto-approve (unless it's a gated category).
_ALLOWED_MARKERS = (
    "/.claude/skills/",
    "/.claude/agents/",
)
# Path fragments for the "hooks" gated category (always reviewed, ask-once-per-session).
_HOOKS_MARKERS = (
    "/tools/hooks/",
)


def _gated_category(norm: str, base: str):
    """Return the ask-once-per-session category for this path, or None."""
    if base.startswith("settings") and base.endswith(".json"):
        return "settings"
    if any(marker in norm for marker in _HOOKS_MARKERS):
        return "hooks"
    return None


def _grant_path(session_id: str, category: str) -> str:
    safe = "".join(c for c in session_id if c.isalnum() or c in "-_")
    return os.path.join(tempfile.gettempdir(), "claude_perm_grants", f"{safe}.{category}.flag")


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
    event = payload.get("hook_event_name")
    session_id = payload.get("session_id") or ""
    category = _gated_category(norm, base)

    # PostToolUse: the edit already succeeded (so the user approved it). Record the
    # per-session grant for this gated category; nothing to decide.
    if event == "PostToolUse":
        if category and session_id:
            try:
                fp = _grant_path(session_id, category)
                os.makedirs(os.path.dirname(fp), exist_ok=True)
                with open(fp, "w") as f:
                    f.write("granted")
            except Exception:
                pass
        return 0

    # PreToolUse below.
    if category:
        # Already approved this category earlier this session? Allow silently.
        if session_id and os.path.exists(_grant_path(session_id, category)):
            return _emit("allow", f"{category}: approved earlier this session")
        # First time this session -> review it (overrides acceptEdits).
        return _emit("ask", f"{category} file - permission machinery, review once per session")

    if any(marker in norm for marker in _ALLOWED_MARKERS):
        return _emit("allow", "curated .claude config (skills/agents, non-settings)")

    return 0  # outside the gated/curated set -> let the user decide (normal flow)


if __name__ == "__main__":
    sys.exit(main())
