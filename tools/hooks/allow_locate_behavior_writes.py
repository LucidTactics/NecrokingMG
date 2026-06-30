#!/usr/bin/env python3
"""Pre/PostToolUse hook governing Write/Edit/MultiEdit to curated .claude config + hook scripts.

  ALLOW (promptless): `.claude/skills/` and `.claude/agents/` (non-settings) — curated config.

  ASK-ONCE-PER-SESSION: `tools/hooks/` (the hook scripts, incl. THIS file). The first edit
  each session prompts ("Allow once"); after approval a PostToolUse flag keyed on session_id
  is written, and the rest of that session is silent for hook scripts.

  DEFER (normal flow): everything else. In particular `settings*.json` is intentionally NOT
  handled here — it must prompt EVERY time, enforced by the `.claude/` acceptEdits carve-out
  plus an explicit `ask` rule in settings.json.

Why a flag and not the dialog's session button: a forced prompt (hook "ask" OR an ask rule)
only ever offers "Allow once" — the "Always allow (this session)" button appears solely on
the unforced default dialog, which acceptEdits skips. So per-session convenience for hook
scripts is implemented with the flag below. Register this script under BOTH PreToolUse and
PostToolUse for matcher Write|Edit|MultiEdit.
"""
import json
import os
import sys
import tempfile

# Tools whose target file we gate on. MultiEdit/Edit/Write all carry file_path.
_FILE_TOOLS = {"Write", "Edit", "MultiEdit"}
# Force-allowed curated config (unless the target is a settings file).
_ALLOWED_MARKERS = (
    "/.claude/skills/",
    "/.claude/agents/",
)
# The hook-scripts folder: ask-once-per-session.
_HOOKS_MARKER = "/tools/hooks/"


def _grant_path(session_id: str) -> str:
    safe = "".join(c for c in session_id if c.isalnum() or c in "-_")
    return os.path.join(tempfile.gettempdir(), "claude_perm_grants", f"{safe}.hooks.flag")


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
    is_hook_script = _HOOKS_MARKER in norm

    # PostToolUse: the edit succeeded (user approved). Record the per-session grant for hooks.
    if event == "PostToolUse":
        if is_hook_script and session_id:
            try:
                fp = _grant_path(session_id)
                os.makedirs(os.path.dirname(fp), exist_ok=True)
                with open(fp, "w") as f:
                    f.write("granted")
            except Exception:
                pass
        return 0

    # PreToolUse below.
    if is_hook_script:
        if session_id and os.path.exists(_grant_path(session_id)):
            return _emit("allow", "hook scripts: approved earlier this session")
        return _emit("ask", "hook script - review once per session")

    if any(marker in norm for marker in _ALLOWED_MARKERS):
        if base.startswith("settings") and base.endswith(".json"):
            return 0  # settings always prompts (carve-out + ask rule) -> defer
        return _emit("allow", "curated .claude config (skills/agents, non-settings)")

    return 0  # everything else -> normal flow


if __name__ == "__main__":
    sys.exit(main())
