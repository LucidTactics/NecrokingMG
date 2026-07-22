"""Audit flipbook filename grid tokens (<cols>x<rows>) against ground truth.

The 2026-07 library import took the source pack's PNG names at face value, but
the pack author sometimes typo'd them (FX_TX_FallingWater_4x3_02.png is really
4x4 — its own PSD and .mat say so). Two independent checks per file:

1. SOURCE TRUTH — match the file against the VisualEffectsURP pack's PSD and
   material names (their tokens are authoritative: the PSD is the working file,
   the material carries the shader's actual rows/cols).
2. CONTENT GUTTERS — flipbook frames are separated by fully-transparent bands;
   a grid divisor is "clean" when every interior boundary line crosses ~no
   alpha. The token is suspect when its boundaries cut content AND some other
   grid is decisively cleaner. (Packed sheets with no gutters can't be judged
   this way and are reported as "content-unclear".)

Report-only by default; --rename applies filename fixes (and prints any
data/flipbooks.json defs whose path needs to follow).

Usage: python tools/audit_flipbook_grids.py [--rename]
"""
import os
import re
import sys
import json

import numpy as np
from PIL import Image

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LIB = os.path.join(REPO, "assets", "Effects", "Flipbooks")
SRC = r"B:\Nightfall\VisualEffectsURP\Assets\ExternalAssets"
FLIPBOOKS_JSON = os.path.join(REPO, "data", "flipbooks.json")

TOKEN_RX = re.compile(r"(?<![0-9])([0-9]{1,2})[xX]([0-9]{1,2})(?![0-9])")


def parse_token(name):
    """Last valid token in a filename, mirroring Flipbook.TryParseGridFromFileName."""
    hits = TOKEN_RX.findall(name)
    for c, r in reversed(hits):
        c, r = int(c), int(r)
        if 1 <= c <= 64 and 1 <= r <= 64 and c * r >= 2:
            return c, r
    return None


def token_key(name):
    """Name with its grid token removed — the join key between our PNG, the
    pack's PSD, and the pack's material (each may carry the token in a
    different position)."""
    base = os.path.splitext(name)[0]
    key = TOKEN_RX.sub("", base)
    key = re.sub(r"__+", "_", key).strip("_").lower()
    # Materials use FX_MT_, textures FX_TX_ — unify the asset-kind prefix.
    key = re.sub(r"^fx_(tx|mt)_", "fx_", key)
    return key


def build_source_truth():
    """token_key -> set of (cols, rows) claimed by pack PSDs / materials."""
    truth = {}
    for dirpath, _, files in os.walk(SRC):
        for f in files:
            ext = os.path.splitext(f)[1].lower()
            if ext not in (".psd", ".mat"):
                continue
            tok = parse_token(os.path.splitext(f)[0])
            if not tok:
                continue
            truth.setdefault(token_key(f), set()).add((tok, ext))
    return truth


def boundary_cost(profile, n, band=2):
    """Mean alpha occupancy in ±band px around each interior boundary of an
    n-way split, normalized by the profile's overall mean. ~0 = clean grid."""
    if n < 2:
        return 0.0
    size = len(profile)
    overall = profile.mean()
    if overall <= 0:
        return 0.0
    total, cnt = 0.0, 0
    for k in range(1, n):
        c = round(size * k / n)
        lo, hi = max(0, c - band), min(size, c + band + 1)
        total += profile[lo:hi].mean()
        cnt += 1
    return (total / cnt) / overall


def content_grids(path, max_n=10):
    """Per-axis boundary costs for each candidate split count."""
    img = Image.open(path).convert("RGBA")
    a = np.asarray(img)[:, :, 3].astype(np.float64)
    if a.max() == 0:  # no alpha channel content — use luminance inverse
        a = 255.0 - np.asarray(img.convert("L")).astype(np.float64)
    col_prof = a.sum(axis=0)  # occupancy per x column -> judges COLS splits
    row_prof = a.sum(axis=1)  # occupancy per y row    -> judges ROWS splits
    cols_cost = {n: boundary_cost(col_prof, n) for n in range(1, max_n + 1)}
    rows_cost = {n: boundary_cost(row_prof, n) for n in range(1, max_n + 1)}
    return cols_cost, rows_cost


CLEAN = 0.02     # boundary band occupancy <2% of average = clean
DECISIVE = 0.20  # token this dirty while an alt is clean = wrong token


def judge_axis(costs, token_n):
    """(verdict, best_clean_alternative). Verdicts: ok / wrong / unclear."""
    tc = costs[token_n]
    if tc <= CLEAN:
        return "ok", None
    clean_alts = [n for n, c in costs.items() if n >= 2 and c <= CLEAN]
    if tc >= DECISIVE and clean_alts:
        # Prefer the largest clean split (n=2 is clean whenever 4 is)
        return "wrong", max(clean_alts)
    return "unclear", max(clean_alts) if clean_alts else None


def main():
    rename = "--rename" in sys.argv
    truth = build_source_truth()

    flip_defs = []
    if os.path.exists(FLIPBOOKS_JSON):
        with open(FLIPBOOKS_JSON, encoding="utf-8") as fh:
            data = json.load(fh)
        flip_defs = data.get("flipbooks", data if isinstance(data, list) else [])

    findings = []
    for dirpath, _, files in os.walk(LIB):
        for f in sorted(files):
            ext = os.path.splitext(f)[1].lower()
            if ext not in (".png", ".tga", ".exr"):
                continue
            full = os.path.join(dirpath, f)
            rel = os.path.relpath(full, REPO).replace("\\", "/")
            tok = parse_token(os.path.splitext(f)[0])
            if not tok:
                findings.append((rel, None, "NO-TOKEN", "", None))
                continue

            src_claims = {t for t, _ in truth.get(token_key(f), set())}
            src_verdict = ""
            src_fix = None
            if src_claims:
                if tok in src_claims and len(src_claims) == 1:
                    src_verdict = "src-ok"
                elif tok not in src_claims:
                    src_verdict = f"SRC-MISMATCH {sorted(src_claims)}"
                    if len(src_claims) == 1:
                        src_fix = next(iter(src_claims))
                else:
                    src_verdict = f"src-ambiguous {sorted(src_claims)}"

            content_verdict = ""
            content_fix = None
            if ext != ".exr":  # PIL can't read EXR; source truth still applies
                try:
                    cc, rc = content_grids(full)
                    cv, calt = judge_axis(cc, tok[0])
                    rv, ralt = judge_axis(rc, tok[1])
                    if cv == "ok" and rv == "ok":
                        content_verdict = "content-ok"
                    elif "wrong" in (cv, rv):
                        fix_c = calt if cv == "wrong" else tok[0]
                        fix_r = ralt if rv == "wrong" else tok[1]
                        content_verdict = f"CONTENT-WRONG -> {fix_c}x{fix_r}"
                        content_fix = (fix_c, fix_r)
                    else:
                        content_verdict = "content-unclear"
                except Exception as ex:
                    content_verdict = f"content-error {ex}"

            bad = src_verdict.startswith("SRC-MISMATCH") or content_fix
            if bad or "unclear" in content_verdict or "ambiguous" in src_verdict:
                fix = src_fix or content_fix
                findings.append((rel, tok, src_verdict, content_verdict, fix))

    print(f"{len(findings)} file(s) flagged:\n")
    renames = []
    for rel, tok, sv, cv, fix in findings:
        tok_s = f"{tok[0]}x{tok[1]}" if tok else "-"
        fix_s = f" FIX->{fix[0]}x{fix[1]}" if fix else ""
        print(f"  {rel}\n      token={tok_s}  {sv}  {cv}{fix_s}")
        if fix:
            old = os.path.basename(rel)
            new = TOKEN_RX.sub(f"{fix[0]}x{fix[1]}", old, count=0)
            # Replace only the LAST token occurrence (the one the parser uses)
            hits = list(TOKEN_RX.finditer(os.path.splitext(old)[0]))
            if hits:
                h = hits[-1]
                new = old[: h.start()] + f"{fix[0]}x{fix[1]}" + old[h.end():]
            renames.append((rel, os.path.join(os.path.dirname(rel), new).replace("\\", "/")))

    if not renames:
        return
    print(f"\n{len(renames)} rename(s) proposed:")
    for old, new in renames:
        print(f"  {old} -> {os.path.basename(new)}")
        used = [d.get("id") for d in flip_defs if d.get("path") == old]
        if used:
            print(f"      !! referenced by flipbooks.json def(s): {used} — update path too")
    if rename:
        for old, new in renames:
            os.rename(os.path.join(REPO, old), os.path.join(REPO, new))
        print("\nrenamed.")
    else:
        print("\n(dry run — pass --rename to apply)")


if __name__ == "__main__":
    main()
