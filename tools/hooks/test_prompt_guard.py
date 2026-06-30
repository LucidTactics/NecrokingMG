#!/usr/bin/env python3
"""Quick assertions for bash_prompt_guard.evaluate. Run: python3 tools/hooks/test_prompt_guard.py

Scope: the hook governs Bash only. Every other tool must `defer` (hook does nothing)."""
import os
import sys

sys.path.insert(0, os.path.dirname(__file__))
import bash_prompt_guard as g

# Use the real project allow/deny lists so the test reflects live behavior.
RULES = g._load_allow_rules()
DENY = g._load_rules("deny")


def ev(tool, inp, mode=""):
    return g.evaluate(tool, inp, mode, RULES, DENY)[0]


def evb(cmd, mode=""):
    return ev("Bash", {"command": cmd}, mode)


def check(label, got, want):
    ok = got == want
    print(("PASS" if ok else "FAIL"), label, "->", got, "" if ok else f"(want {want})")
    return ok


fails = 0

# --- Layer 1: bash redirects (fire even though cat/grep are allow-listed) ---------
fails += not check("bash cat -> deny", evb("cat foo.txt"), "deny")
fails += not check("bash grep -> deny", evb("grep -r x ."), "deny")
fails += not check("cd&&grep -> deny", evb("cd sub && grep x f"), "deny")
fails += not check("grep in pipe -> allow (all parts allow-listed)",
                   evb("dotnet build | grep error"), "allow")
fails += not check("py_compile -> allow", evb("python -m py_compile x.py"), "allow")
fails += not check("ast.parse -> deny",
                   evb("python -c 'import ast; ast.parse(open(1).read())'"), "deny")

# --- Layer 2: allow-listed -> force-allow; otherwise deny -------------------------
fails += not check("git status -> allow", evb("git status"), "allow")
fails += not check("dotnet build -> allow", evb("dotnet build Necroking/Necroking.csproj"), "allow")
fails += not check("ls -> allow", evb("ls -la"), "allow")
fails += not check("taskkill -> deny (not allow-listed)", evb("taskkill /F /IM x.exe"), "deny")
fails += not check("curl random -> deny", evb("curl https://evil.example"), "deny")
fails += not check("devctl -> allow", evb("python tools/devctl.py shot fight"), "allow")

# --- Compound commands: allow only when EVERY segment is allow-listed -------------
fails += not check("the compound that prompted -> allow",
                   evb('cd "$CLAUDE_PROJECT_DIR"; git status -s; echo "---"; '
                       'python -m py_compile tools/hooks/bash_prompt_guard.py && echo "COMPILE_OK"'),
                   "allow")
fails += not check("compound with one bad segment -> deny",
                   evb("git status; taskkill /F /IM x.exe"), "deny")
fails += not check("git add && commit -> allow", evb('git add -A && git commit -m "x"'), "allow")
fails += not check("quoted semicolon stays one segment -> allow",
                   evb('echo "a; rm -rf b"'), "allow")

# --- robocopy: allow when DEST is inside the project, else prompt/deny -------------
PROJ = r"C:\Users\Johan\source\repos\Lucid\NecrokingMG"


def evb_proj(cmd, proj=PROJ):
    return g.evaluate("Bash", {"command": cmd}, "", RULES, DENY, project_dir=proj)[0]


fails += not check("robocopy into project -> allow",
                   evb_proj(r'robocopy "G:\drive\assets\Sprites" '
                            r'"C:\Users\Johan\source\repos\Lucid\NecrokingMG\assets\Sprites" '
                            r'Animals.png /NJH /FP'), "allow")
fails += not check("robocopy relative dest into project -> allow",
                   evb_proj(r'robocopy "G:\src" assets\maps default.json'), "allow")
fails += not check("MSYS_NO_PATHCONV prefix + robocopy into project -> allow",
                   evb_proj(r'MSYS_NO_PATHCONV=1 robocopy "G:\src" '
                            r'"C:\Users\Johan\source\repos\Lucid\NecrokingMG\assets\Sprites" '
                            r'a.png /NJH'), "allow")
fails += not check("robocopy dest outside project -> deny",
                   evb_proj(r'robocopy "G:\src" "C:\Windows\System32" x.dll'), "deny")
fails += not check("robocopy /MIR into project -> deny (destructive)",
                   evb_proj(r'robocopy "G:\src" '
                            r'"C:\Users\Johan\source\repos\Lucid\NecrokingMG\assets" /MIR'), "deny")
fails += not check("robocopy sibling-prefix dir -> deny (not real containment)",
                   evb_proj(r'robocopy "G:\src" '
                            r'"C:\Users\Johan\source\repos\Lucid\NecrokingMG-evil" x'), "deny")
fails += not check("robocopy && taskkill -> deny (bad second segment)",
                   evb_proj(r'robocopy "G:\src" '
                            r'"C:\Users\Johan\source\repos\Lucid\NecrokingMG\assets" x && taskkill /F /IM y'),
                   "deny")

# --- Substitution / heredoc -> defer (not force-allowed, not denied) --------------
fails += not check("command substitution -> defer", evb("echo $(rm -rf /)"), "defer")
fails += not check("backtick substitution -> defer", evb("echo `whoami`"), "defer")
fails += not check("heredoc commit -> defer", evb("git commit -F - <<'EOF'\nmsg\nEOF"), "defer")

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
