#!/usr/bin/env python3
"""Generate a self-contained HTML power-progression report from
power_progression scenario results.

Usage (from repo root):
    python tools/power_report/make_report.py [results.json] [out.html]

Defaults: bin/Debug/log/power_progression_results.json -> tools/power_report/power_report.html

One row per scripted matchup: the intended outcome ("1 boar should beat 4 male
deer"), the measured win rate over N parallel-arena trials, average casualties
on both sides, and a PASS/FAIL verdict (does the majority winner match the
design intent?). Icons come from tools/balance_report/icons (crop_icons.ps1)
and are embedded as base64 — the file is fully self-contained.
"""
import base64
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))

RESULTS = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "bin/Debug/log/power_progression_results.json")
OUT = sys.argv[2] if len(sys.argv) > 2 else os.path.join(HERE, "power_report.html")
ICONS = os.path.join(ROOT, "tools/balance_report/icons")

with open(RESULTS, encoding="utf-8") as f:
    run = json.load(f)
with open(os.path.join(ROOT, "data/units.json"), encoding="utf-8") as f:
    defs = {u["id"]: u for u in json.load(f)["units"]}

_icon_cache = {}


def icon_b64(uid):
    if uid not in _icon_cache:
        path = os.path.join(ICONS, uid + ".png")
        if os.path.exists(path):
            with open(path, "rb") as fh:
                _icon_cache[uid] = base64.b64encode(fh.read()).decode()
        else:
            _icon_cache[uid] = None
    return _icon_cache[uid]


def name_of(uid):
    return defs.get(uid, {}).get("name", uid)


def side_html(parts):
    bits = []
    for p in parts:
        b64 = icon_b64(p["unit"])
        img = f'<img src="data:image/png;base64,{b64}" alt="">' if b64 else ""
        bits.append(f'<span class="part">{img}<b>{p["count"]}&times;</b> {name_of(p["unit"])}</span>')
    return '<span class="plus">+</span>'.join(bits)


def spawned(parts):
    return sum(p["count"] for p in parts)


rows = []
pass_count = fail_count = 0
for m in run.get("matchups", []):
    trials = m.get("trials", [])
    n = len(trials)
    decisive = m["winsA"] + m["winsB"]
    win_pct = round(100 * m["winsA"] / decisive) if decisive else None
    ok = m.get("pass", False)
    if ok:
        pass_count += 1
    else:
        fail_count += 1
    verdict = "PASS" if ok else "FAIL"
    expect = "A wins" if m.get("expectedWinner") == "A" else "B wins"
    sa, sb = spawned(m["sideA"]), spawned(m["sideB"])
    cas_a = m.get("avgCasualtiesA", 0.0)
    cas_b = m.get("avgCasualtiesB", 0.0)
    note = m.get("note", "")
    note_html = f'<div class="note-inline">{note}</div>' if note else ""
    wld = f'{m["winsA"]}-{m["winsB"]}-{m["draws"]}'
    pct_html = f"{win_pct}%" if win_pct is not None else "&mdash;"
    # per-trial dots: green = A won, amber = B won, grey = draw
    dots = "".join(
        f'<span class="dot {t["winner"]}" title="trial {i}: {t["winner"]}, '
        f'A lost {t["casualtiesA"]}/{sa}, B lost {t["casualtiesB"]}/{sb}, {t["duration"]:.0f}s"></span>'
        for i, t in enumerate(trials))
    rows.append(f"""<tr class="{'ok' if ok else 'bad'}">
<td class="verdict"><span class="chip {'p' if ok else 'f'}">{verdict}</span></td>
<td class="side a">{side_html(m['sideA'])}{note_html}</td>
<td class="vs">vs</td>
<td class="side b">{side_html(m['sideB'])}</td>
<td class="expect">{expect}</td>
<td class="pct"><b>{pct_html}</b><div class="s">A wins &middot; {wld} W-L-D</div></td>
<td class="dots">{dots}</td>
<td class="cas">{cas_a:.1f} / {sa}</td>
<td class="cas">{cas_b:.1f} / {sb}</td>
<td class="dur">{m.get('avgDuration', 0):.0f}s</td>
</tr>""")

total = pass_count + fail_count
summary = f"{pass_count}/{total} matchups match the intended progression"

html = f"""<!doctype html>
<html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Necroking — Power Progression Report</title>
<style>
:root {{
  --surface: #fcfcfb; --page: #f9f9f7; --ink: #0b0b0b; --ink2: #52514e;
  --muted: #898781; --grid: #e1e0d9; --ring: rgba(11,11,11,.10);
  --pass: #1d7a3c; --pass-bg: #d9f0e0; --fail: #b3261e; --fail-bg: #f8dcda;
  --a-ink: #1c5cab; --b-ink: #a86e00; --draw: #b7b5ad;
}}
@media (prefers-color-scheme: dark) {{
  :root {{
    --surface: #1a1a19; --page: #0d0d0d; --ink: #ffffff; --ink2: #c3c2b7;
    --muted: #898781; --grid: #2c2c2a; --ring: rgba(255,255,255,.10);
    --pass: #6fd695; --pass-bg: #14331e; --fail: #f2827b; --fail-bg: #3d1512;
    --a-ink: #6da7ec; --b-ink: #eda100; --draw: #55534c;
  }}
}}
* {{ box-sizing: border-box; }}
body {{ margin: 0; padding: 24px; background: var(--page); color: var(--ink);
  font: 14px/1.45 system-ui, -apple-system, "Segoe UI", sans-serif; }}
h1 {{ font-size: 20px; margin: 0 0 4px; }}
.sub {{ color: var(--ink2); margin: 0 0 16px; }}
.wrap {{ overflow-x: auto; background: var(--surface); border: 1px solid var(--ring);
  border-radius: 10px; padding: 16px; }}
table {{ border-collapse: collapse; width: 100%; }}
th, td {{ padding: 8px 10px; text-align: left; border-bottom: 1px solid var(--grid);
  vertical-align: middle; }}
th {{ color: var(--ink2); font-size: 12px; font-weight: 600; white-space: nowrap; }}
tr:last-child td {{ border-bottom: none; }}
.chip {{ display: inline-block; padding: 2px 10px; border-radius: 999px; font-weight: 700;
  font-size: 12px; }}
.chip.p {{ color: var(--pass); background: var(--pass-bg); }}
.chip.f {{ color: var(--fail); background: var(--fail-bg); }}
.side {{ white-space: nowrap; }}
.side.a .part b {{ color: var(--a-ink); }}
.side.b .part b {{ color: var(--b-ink); }}
.part {{ display: inline-flex; align-items: center; gap: 6px; }}
.part img {{ max-width: 44px; max-height: 34px; image-rendering: pixelated; }}
.plus {{ margin: 0 8px; color: var(--muted); }}
.vs {{ color: var(--muted); font-size: 12px; }}
.pct .s {{ font-size: 11px; color: var(--muted); font-weight: 400; }}
.dots {{ white-space: nowrap; }}
.dot {{ display: inline-block; width: 10px; height: 10px; border-radius: 50%;
  margin-right: 3px; }}
.dot.A {{ background: var(--a-ink); }}
.dot.B {{ background: var(--b-ink); }}
.dot.draw {{ background: var(--draw); }}
.cas, .dur {{ white-space: nowrap; }}
.note-inline {{ font-size: 11px; color: var(--muted); font-style: italic; }}
.totals {{ margin: 14px 0 0; font-weight: 600; }}
.totals .p {{ color: var(--pass); }} .totals .f {{ color: var(--fail); }}
.note {{ color: var(--ink2); font-size: 12.5px; max-width: 860px; }}
</style></head>
<body>
<h1>Power Progression Report</h1>
<p class="sub">Does each scripted matchup come out the way the design intends?
Side <b style="color:var(--a-ink)">A</b> (the hero) vs side
<b style="color:var(--b-ink)">B</b> (the challenger); "expected" is the intended
winner. Generated {run.get('generated', '?')} &middot; {run.get('trialsPerMatchup', '?')}
parallel-arena trials per matchup &middot; draws after {run.get('fightTimeout', '?')} game-seconds.</p>
<div class="wrap">
<table>
<tr><th></th><th>Side A</th><th></th><th>Side B</th><th>Expected</th>
<th>A win rate</th><th>Trials</th><th>avg A casualties</th><th>avg B casualties</th><th>avg fight</th></tr>
{''.join(rows)}
</table>
<p class="totals"><span class="p">{pass_count} pass</span> &middot;
<span class="f">{fail_count} fail</span> &mdash; {summary}.</p>
</div>
<p class="note"><b>Method.</b> All trials of a matchup run simultaneously, one per arena
cell on a 5&times;2 grid separated by deep-water dividers (unpathable). Both sides spawn as
leash-less attack-closest units (no horde AI, morale forced fearless), melee swings resolve
instantly, and spawn side / spawn order / faction alternate across trials to cancel
systematic biases. A matchup PASSes when the majority winner of decisive trials matches the
intended winner. Casualties are per-trial deaths out of the units fielded
("1.8 / 3" = on average 1.8 of the 3 fielded units died).</p>
</body></html>
"""

with open(OUT, "w", encoding="utf-8") as f:
    f.write(html)
print(f"wrote {OUT} ({pass_count} pass / {fail_count} fail of {total})")
