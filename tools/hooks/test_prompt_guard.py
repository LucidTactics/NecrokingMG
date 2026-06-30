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

# --- Read-only search now falls through to the fast-allow (no longer redirected) -----
fails += not check("bash cat -> allow (read-only)", evb("cat foo.txt"), "allow")
fails += not check("bash grep -> allow (read-only)", evb("grep -r x ."), "allow")
fails += not check("cd&&grep -> allow (read-only)", evb("cd sub && grep x f"), "allow")
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

# --- Read-only fast-allow: side-effect-free commands wave through even if not on the
#     allow-list; anything that writes a file or controls processes/power does not ------
fails += not check("wc -> allow (read-only, not allow-listed)", evb("wc -l foo.txt"), "allow")
fails += not check("sort | uniq -> allow (read-only)", evb("sort a.txt | uniq -c"), "allow")
fails += not check("date -> allow (read-only)", evb("date +%s"), "allow")
fails += not check("truncate -> deny (file write, not allow-listed)",
                   evb("truncate -s0 foo.txt"), "deny")
fails += not check("tee -> deny (file write)", evb("echo hi | tee out.txt"), "deny")
fails += not check("rm -> deny (file write)", evb("rm -rf build"), "deny")
fails += not check("taskkill -> deny (process control)", evb("taskkill /F /IM x.exe"), "deny")
fails += not check("kill -> deny (process control)", evb("kill -9 1234"), "deny")
fails += not check("robocopy outside -> deny (file write, not via fast-allow)",
                   evb(r'robocopy "G:\src" "C:\Windows\System32" x.dll'), "deny")
fails += not check("find search -> allow (read-only, no mutating flag)",
                   evb('find . -name "*.cs" -type f'), "allow")
fails += not check("find -delete -> ask (mutating flag surfaces despite find:* allow)",
                   evb("find . -name '*.tmp' -delete"), "ask")
fails += not check("find -exec -> ask (mutating flag surfaces despite find:* allow)",
                   evb("find . -name '*.cs' -exec sed -i s/a/b/ {} +"), "ask")

# --- Compound commands: allow only when EVERY segment is allow-listed -------------
fails += not check("the compound that prompted -> allow",
                   evb('cd "$CLAUDE_PROJECT_DIR"; git status -s; echo "---"; '
                       'python -m py_compile tools/hooks/bash_prompt_guard.py && echo "COMPILE_OK"'),
                   "allow")
fails += not check("compound with one bad segment -> deny",
                   evb("git status; taskkill /F /IM x.exe"), "deny")
fails += not check("git add && commit -> allow", evb('git add -A && git commit -m "x"'), "allow")

# --- git push (and other remote-push vectors) must prompt; all other git auto-allows --
fails += not check("git push -> ask", evb("git push"), "ask")
fails += not check("git push origin master -> ask", evb("git push origin master"), "ask")
fails += not check("git -c k=v push -> ask (skips global opts)",
                   evb("git -c http.sslVerify=false push --force"), "ask")
fails += not check("git push in compound -> ask",
                   evb("git status && git push origin master"), "ask")
fails += not check("git send-pack -> ask", evb("git send-pack origin HEAD"), "ask")
fails += not check("git status -> still allow", evb("git status"), "allow")
fails += not check("git log --grep=push -> allow (not a push)",
                   evb('git log --grep=push'), "allow")
fails += not check("git commit pushy message -> allow (subcommand is commit)",
                   evb('git commit -m "push later"'), "allow")
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
