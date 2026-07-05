#!/bin/sh
# Fail-closed launcher for bash_prompt_guard.py.
#
# Why: the hook used to invoke `python …` directly. On a machine where python is
# missing (or only the Windows Store shim exists), the hook errored out and Claude
# Code treated that as "no decision" — silently running with NO Bash guard at all.
# That unguarded state enabled the 2026-07-05 assets/ wipe (an unflagged junction +
# `git worktree remove --force`).
#
# This wrapper tries several interpreters (env override first, then launchers, then
# known embeddable installs). If NONE work it emits an "ask" decision for every Bash
# call — loud and safe (fail CLOSED), instead of silently unguarded (fail open).

GUARD="$(dirname "$0")/bash_prompt_guard.py"
PAYLOAD="$(cat)"

for P in "$CLAUDE_GUARD_PYTHON" \
         py python3 python \
         "/c/Users/Raymo/Tools/python-3.11-embed/python.exe" \
         "C:/Users/Raymo/Tools/python-3.11-embed/python.exe"; do
    [ -n "$P" ] || continue
    # Probe by executing: the Windows Store python shim exists on PATH but only
    # prints an install nag and fails, so `command -v` alone is not a valid probe.
    if "$P" -c "import sys" >/dev/null 2>&1; then
        printf '%s' "$PAYLOAD" | "$P" "$GUARD"
        exit $?
    fi
done

printf '%s' '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"ask","permissionDecisionReason":"bash_prompt_guard could not run: no working python found (the Windows Store shim does not count). Every Bash call will prompt until this is fixed - install python, or set CLAUDE_GUARD_PYTHON to a python.exe (an embeddable build works), or add its location to tools/hooks/run_guard.sh. Failing CLOSED rather than running with no Bash guard."}}'
exit 0
