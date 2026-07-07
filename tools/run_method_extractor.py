"""Run the Roslyn MethodExtractor (tools/MethodExtractor) and report output stats.

Usage: python tools/run_method_extractor.py <outDir>
Builds are done separately via `dotnet build`; this just launches the built exe.
"""
import subprocess
import sys
import os
import glob

repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
out_dir = sys.argv[1] if len(sys.argv) > 1 else os.path.join(repo, "cache", "method_extract")

exe_candidates = glob.glob(os.path.join(repo, "tools", "MethodExtractor", "bin", "**", "MethodExtractor.exe"), recursive=True)
if not exe_candidates:
    print("MethodExtractor.exe not found - run: dotnet build tools/MethodExtractor/MethodExtractor.csproj")
    sys.exit(2)
exe = exe_candidates[0]

result = subprocess.run([exe, repo, out_dir], capture_output=True, text=True)
print(result.stdout)
if result.returncode != 0:
    print(result.stderr)
    sys.exit(result.returncode)

batches = glob.glob(os.path.join(out_dir, "batches", "*.json"))
print(f"out dir: {out_dir}")
print(f"batches: {len(batches)}")
for f in ("catalog.json", "auto_scenarios.json"):
    p = os.path.join(out_dir, f)
    print(f"{f}: {os.path.getsize(p)} bytes" if os.path.exists(p) else f"{f}: MISSING")
