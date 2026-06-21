#!/usr/bin/env python3
"""Quick assertions for bash_prompt_guard.evaluate. Run: python3 tools/hooks/test_prompt_guard.py

Scope: the hook governs Bash only. Every other tool must `defer` (hook does nothing)."""
import os
import sys

sys.path.insert(0, os.path.dirname(__file__))
import bash_prompt_guard as g

# Use the real project allow-list so the test reflects live behavior.
RULES = g._load_allow_rules()


def ev(tool, inp, mode=""):
    return g.evaluate(tool, inp, mode, RULES)[0]


def check(label, got, want):
    ok = got == want
    print(("PASS" if ok else "FAIL"), label, "->", got, "" if ok else f"(want {want})")
    return ok


fails = 0

# --- Layer 1: bash redirects (fire even though cat/grep are allow-listed) ---------
fails += not check("bash cat -> deny", ev("Bash", {"command": "cat foo.txt"}), "deny")
fails += not check("bash grep -> deny", ev("Bash", {"command": "grep -r x ."}), "deny")
fails += not check("cd&&grep -> deny", ev("Bash", {"command": "cd sub && grep x f"}), "deny")
fails += not check("grep in pipe -> not layer1 (allow-listed)",
                   ev("Bash", {"command": "dotnet build | grep error"}), "defer")
fails += not check("py_compile -> allow-listed defer",
                   ev("Bash", {"command": "python -m py_compile x.py"}), "defer")
fails += not check("ast.parse -> deny",
                   ev("Bash", {"command": "python -c 'import ast; ast.parse(open(1).read())'"}), "deny")

# --- Layer 2: bash deny-by-default vs allow-list ----------------------------------
fails += not check("git status -> allow-listed defer", ev("Bash", {"command": "git status"}), "defer")
fails += not check("dotnet build -> allow-listed defer",
                   ev("Bash", {"command": "dotnet build Necroking/Necroking.csproj"}), "defer")
fails += not check("ls -> allow-listed defer", ev("Bash", {"command": "ls -la"}), "defer")
fails += not check("taskkill -> deny (not allow-listed)", ev("Bash", {"command": "taskkill /F /IM x.exe"}), "deny")
fails += not check("curl random -> deny", ev("Bash", {"command": "curl https://evil.example"}), "deny")
fails += not check("devctl -> allow-listed defer", ev("Bash", {"command": "python tools/devctl.py shot fight"}), "defer")

# --- Every non-Bash tool is left alone (defer), no matter what --------------------
for tool, inp in [
    ("Edit", {"file_path": "Necroking/Game1.cs"}),
    ("Write", {"file_path": "C:/Users/Johan/.claude/memory/x.md"}),
    ("NotebookEdit", {"notebook_path": "x.ipynb"}),
    ("Read", {"file_path": "x"}),
    ("Grep", {"pattern": "x"}),
    ("WebFetch", {"url": "https://nope.example/x"}),
    ("Skill", {"skill": "code-review"}),
    ("LSP", {"op": "definition"}),
    ("mcp__Claude_Preview__preview_eval", {"code": "1"}),
    ("mcp__computer-use__left_click", {"x": 1}),
]:
    fails += not check(f"{tool} -> defer (not governed)", ev(tool, inp), "defer")

# --- Modes defer even for Bash ----------------------------------------------------
fails += not check("bypass mode -> defer", ev("Bash", {"command": "rm -rf /"}, "bypassPermissions"), "defer")
fails += not check("plan mode -> defer", ev("Bash", {"command": "taskkill x"}, "plan"), "defer")

print()
print("ALL PASS" if fails == 0 else f"{fails} FAILURES")
sys.exit(1 if fails else 0)
