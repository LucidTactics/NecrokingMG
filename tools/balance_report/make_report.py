#!/usr/bin/env python3
"""Generate a self-contained HTML balance-matrix report from balance_matrix
scenario results.

Usage (from repo root, after tools/balance_report/crop_icons.ps1):
    python tools/balance_report/make_report.py [results.json] [out.html]

Defaults: bin/Debug/log/balance_results.json -> tools/balance_report/balance_report.html

The matrix: rows and columns are units (sprite icons as labels); cell (R, C)
shows the squad counts that put the R-vs-C fight at ~50% win rate ("3v5" =
3 R beat 5 C about half the time). Background color encodes who needs more
bodies: blue = the COLUMN unit needed more (row unit is stronger), amber =
the ROW unit needed more. Depth of color = how lopsided (2x, 3x, capped).
Diagonal cells show the mirror-match win rate (harness sanity check, ~50%).
All icons are embedded as base64 — the file is fully self-contained.
"""
import base64
import json
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))

RESULTS = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "bin/Debug/log/balance_results.json")
OUT = sys.argv[2] if len(sys.argv) > 2 else os.path.join(HERE, "balance_report.html")
ICONS = os.path.join(HERE, "icons")

with open(RESULTS, encoding="utf-8") as f:
    run = json.load(f)
with open(os.path.join(ROOT, "data/units.json"), encoding="utf-8") as f:
    defs = {u["id"]: u for u in json.load(f)["units"]}

units = run["units"]
base = run.get("baseCount", 5)
names = {u: defs.get(u, {}).get("name", u) for u in units}

pair_map = {}
for p in run.get("pairs", []):
    pair_map[(p["unitA"], p["unitB"])] = p


def icon_b64(uid):
    path = os.path.join(ICONS, uid + ".png")
    if not os.path.exists(path):
        return None
    with open(path, "rb") as fh:
        return base64.b64encode(fh.read()).decode()


def win_frac_a(cfg):
    d = cfg["winsA"] + cfg["winsB"]
    return cfg["winsA"] / d if d else None


def cell_for(r, c):
    """Cell data for row unit r vs column unit c. Pair results are stored
    unordered (A = earlier roster index); mirror counts/wins when needed."""
    p = pair_map.get((r, c))
    flip = False
    if p is None:
        p = pair_map.get((c, r))
        flip = p is not None
    if p is None or p.get("final") is None:
        return None
    f = p["final"]
    count_r = f["countB"] if flip else f["countA"]
    count_c = f["countA"] if flip else f["countB"]
    wins_r = f["winsB"] if flip else f["winsA"]
    wins_c = f["winsA"] if flip else f["winsB"]
    decisive = wins_r + wins_c
    return {
        "res": p.get("resolution", ""),
        "count_r": count_r,
        "count_c": count_c,
        "wins_r": wins_r,
        "wins_c": wins_c,
        "draws": f.get("draws", 0),
        "decisive": decisive,
        "win_r": (wins_r / decisive) if decisive else None,
        "dur": f.get("avgDuration", 0.0),
        "surv": f.get("avgWinnerSurvivors", 0.0),
        "configs": len(p.get("configs", [])),
    }


def bucket(cell):
    """CSS class: the WINNER's axis color (blue = row unit wins, amber = column
    unit wins — matching the axis label colors), intensity = how lopsided the
    body-count ratio is. The winner is the side that needs fewer bodies."""
    if cell is None:
        return "miss"
    if cell["res"] == "stalemate":
        return "stale"
    if cell["res"] == "cap":
        # the searched (weaker) side is the one at max count
        return "y3" if cell["count_r"] > cell["count_c"] else "b3"
    v = math.log2(cell["count_c"] / cell["count_r"])
    a = abs(v)
    if a < 0.263:  # counts within ~1.2x — tint by win rate if it's off-center
        p = cell["win_r"]
        if p is None or 0.4 <= p <= 0.6:
            return "n0"
        step = "2" if abs(p - 0.5) > 0.25 else "1"
        return ("b" if p > 0.5 else "y") + step
    step = "1" if a < 1.0 else ("2" if a < 1.585 else "3")
    return ("b" if v > 0 else "y") + step


def cell_html(r, c):
    if r == c:
        cell = cell_for(r, c)
        if cell is None or cell["win_r"] is None:
            return '<td class="cell miss">&hellip;</td>'
        pct = round(cell["win_r"] * 100)
        tip = (f"{names[r]} mirror sanity check&#10;{base}v{base}: "
               f"{cell['wins_r']}-{cell['wins_c']} ({pct}% / expect ~50%)"
               f"&#10;{cell['decisive']} decisive, {cell['draws']} draws")
        return (f'<td class="cell diag" data-tip="{tip}">'
                f'<div class="v">{base}v{base}</div><div class="s">{pct}%</div></td>')

    cell = cell_for(r, c)
    cls = bucket(cell)
    if cell is None:
        return '<td class="cell miss">&hellip;</td>'
    if cls == "stale":
        return (f'<td class="cell stale" data-tip="All trials were draws — '
                f'neither side can kill the other.">'
                f'<div class="v">draw</div></td>')
    plus_r = "+" if cell["res"] == "cap" and cell["count_r"] > cell["count_c"] else ""
    plus_c = "+" if cell["res"] == "cap" and cell["count_c"] > cell["count_r"] else ""
    pct = round(cell["win_r"] * 100) if cell["win_r"] is not None else "?"
    tip = (f"{cell['count_r']}{plus_r} {names[r]} vs {cell['count_c']}{plus_c} {names[c]}"
           f"&#10;row wins {cell['wins_r']} / col wins {cell['wins_c']}"
           f" / draws {cell['draws']}"
           f"&#10;avg fight {cell['dur']:.0f}s, winner keeps {cell['surv']:.1f} alive"
           f"&#10;resolution: {cell['res']} ({cell['configs']} counts tested)")
    if cell["res"] == "cap":
        sub = "row can't win" if plus_r else "col can't win"
    else:
        sub = f"{pct}%"
    return (f'<td class="cell {cls}" data-tip="{tip}">'
            f'<div class="v">{cell["count_r"]}{plus_r}v{cell["count_c"]}{plus_c}</div>'
            f'<div class="s">{sub}</div></td>')


def head_cell(uid):
    b64 = icon_b64(uid)
    img = f'<img src="data:image/png;base64,{b64}" alt="">' if b64 else ""
    return f'<th class="colhead"><div class="hc">{img}<span>{names[uid]}</span></div></th>'


def row_head(uid):
    b64 = icon_b64(uid)
    img = f'<img src="data:image/png;base64,{b64}" alt="">' if b64 else ""
    return f'<th class="rowhead"><div class="hr">{img}<span>{names[uid]}</span></div></th>'


matrix_rows = []
for r in units:
    tds = "".join(cell_html(r, c) for c in units)
    matrix_rows.append(f"<tr>{row_head(r)}{tds}</tr>")

# ---- detail table (the accessible/table view) ----
detail_rows = []
for p in run.get("pairs", []):
    f = p.get("final")
    if f is None:
        continue
    d = f["winsA"] + f["winsB"]
    pct = f"{round(100 * f['winsA'] / d)}%" if d else "—"
    detail_rows.append(
        f"<tr><td>{names[p['unitA']]}</td><td>{names[p['unitB']]}</td>"
        f"<td>{f['countA']}v{f['countB']}</td><td>{pct}</td>"
        f"<td>{f['winsA']}-{f['winsB']}-{f.get('draws', 0)}</td>"
        f"<td>{f.get('avgDuration', 0):.0f}s</td>"
        f"<td>{f.get('avgWinnerSurvivors', 0):.1f}</td>"
        f"<td>{p.get('resolution', '')}</td></tr>")

done = sum(1 for p in run.get("pairs", []) if p.get("final"))
total_expected = len(units) * (len(units) + 1) // 2
partial = ("" if done >= total_expected else
           f'<p class="partial">Partial results: {done}/{total_expected} pairs measured so far.</p>')

html = f"""<!doctype html>
<html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Necroking — Animal Zombie Balance Matrix</title>
<style>
:root {{
  --surface: #fcfcfb; --page: #f9f9f7; --ink: #0b0b0b; --ink2: #52514e;
  --muted: #898781; --grid: #e1e0d9; --ring: rgba(11,11,11,.10);
  --n0: #f0efec; --diag: #f7f6f3;
  --b1: #b7d3f6; --b2: #6da7ec; --b3: #2a78d6;
  --y1: #f5e2ae; --y2: #f0c159; --y3: #eda100;
  --ink-on-3: #ffffff;
  --row-ink: #1c5cab; --col-ink: #a86e00;
}}
@media (prefers-color-scheme: dark) {{
  :root {{
    --surface: #1a1a19; --page: #0d0d0d; --ink: #ffffff; --ink2: #c3c2b7;
    --muted: #898781; --grid: #2c2c2a; --ring: rgba(255,255,255,.10);
    --n0: #383835; --diag: #232322;
    --b1: #1d3a61; --b2: #1c5cab; --b3: #3987e5;
    --y1: #4a3a10; --y2: #8a6200; --y3: #c98500;
    --ink-on-3: #0b0b0b;
    --row-ink: #6da7ec; --col-ink: #eda100;
  }}
}}
* {{ box-sizing: border-box; }}
body {{ margin: 0; padding: 24px; background: var(--page); color: var(--ink);
  font: 14px/1.45 system-ui, -apple-system, "Segoe UI", sans-serif; }}
h1 {{ font-size: 20px; margin: 0 0 4px; }}
.sub {{ color: var(--ink2); margin: 0 0 16px; }}
.partial {{ color: var(--y3); font-weight: 600; }}
.wrap {{ overflow-x: auto; background: var(--surface); border: 1px solid var(--ring);
  border-radius: 10px; padding: 16px; }}
table.matrix {{ border-collapse: separate; border-spacing: 2px; }}
.matrix th {{ font-weight: 600; color: var(--ink2); }}
.colhead .hc {{ display: flex; flex-direction: column; align-items: center; gap: 4px;
  width: 84px; padding: 4px 2px; }}
.colhead img {{ max-width: 56px; max-height: 44px; image-rendering: pixelated; }}
.colhead span, .rowhead span {{ font-size: 11px; line-height: 1.15; text-align: center; }}
.colhead span {{ color: var(--col-ink); font-weight: 700; }}
.rowhead span {{ color: var(--row-ink); font-weight: 700; }}
.rowhead .hr {{ display: flex; align-items: center; gap: 8px; padding: 2px 8px 2px 2px;
  max-width: 170px; }}
.rowhead img {{ max-width: 56px; max-height: 40px; image-rendering: pixelated; }}
.corner {{ min-width: 120px; }}
td.cell {{ width: 84px; height: 56px; text-align: center; vertical-align: middle;
  border-radius: 6px; background: var(--n0); cursor: default; }}
td.cell:hover {{ outline: 2px solid var(--ink2); outline-offset: -2px; }}
td.cell .v {{ font-weight: 700; font-size: 15px; }}
td.cell .s {{ font-size: 11px; opacity: .75; }}
td.diag {{ background: var(--diag); color: var(--muted); }}
td.b1 {{ background: var(--b1); }} td.b2 {{ background: var(--b2); }}
td.b3 {{ background: var(--b3); color: var(--ink-on-3); }}
td.y1 {{ background: var(--y1); }} td.y2 {{ background: var(--y2); }}
td.y3 {{ background: var(--y3); color: var(--ink-on-3); }}
td.miss {{ color: var(--muted); }}
td.stale {{ background: repeating-linear-gradient(45deg, var(--n0), var(--n0) 6px,
  var(--grid) 6px, var(--grid) 12px); }}
.legend {{ display: flex; align-items: center; gap: 6px; margin: 14px 0 4px; flex-wrap: wrap; }}
.legend .sw {{ width: 34px; height: 18px; border-radius: 4px; border: 1px solid var(--ring); }}
.legend span {{ font-size: 12px; color: var(--ink2); }}
.note {{ color: var(--ink2); font-size: 12.5px; max-width: 860px; }}
h2 {{ font-size: 16px; margin: 28px 0 8px; }}
table.detail {{ border-collapse: collapse; background: var(--surface);
  border: 1px solid var(--ring); border-radius: 8px; }}
table.detail th, table.detail td {{ padding: 5px 12px; text-align: left; font-size: 12.5px;
  border-bottom: 1px solid var(--grid); }}
table.detail th {{ color: var(--ink2); }}
#tip {{ position: fixed; display: none; background: var(--ink); color: var(--page);
  padding: 8px 10px; border-radius: 6px; font-size: 12px; max-width: 320px;
  pointer-events: none; white-space: pre-line; z-index: 10; }}
</style></head>
<body>
<h1>Animal Zombie Balance Matrix</h1>
<p class="sub">What body counts make a <b style="color:var(--row-ink)">row</b>-unit vs
<b style="color:var(--col-ink)">column</b>-unit fight a coin flip? Cell shows the ~50% matchup
(row count v column count) &mdash; whoever needs fewer bodies is winning, and the cell wears
their color. Generated {run.get('generated', '?')} &middot; band 40&ndash;60% win rate &middot;
up to {run.get('maxTrials', '?')} trials per count.</p>
{partial}
<div class="wrap">
<table class="matrix">
<tr><th class="corner"></th>{''.join(head_cell(u) for u in units)}</tr>
{''.join(matrix_rows)}
</table>
<div class="legend">
  <span>Cell color = who wins (the side that needs fewer bodies), matching the axis label colors.</span>
  <div class="sw" style="background:var(--b3); margin-left:10px"></div>
  <div class="sw" style="background:var(--b2)"></div>
  <div class="sw" style="background:var(--b1)"></div>
  <span><b style="color:var(--row-ink)">row unit wins</b> by 3x+ / 2&ndash;3x / under 2x</span>
  <div class="sw" style="background:var(--n0); margin-left:14px"></div>
  <span>even (~1:1)</span>
  <div class="sw" style="background:var(--y1); margin-left:14px"></div>
  <div class="sw" style="background:var(--y2)"></div>
  <div class="sw" style="background:var(--y3)"></div>
  <span><b style="color:var(--col-ink)">column unit wins</b> by under 2x / 2&ndash;3x / 3x+</span>
</div>
</div>
<p class="note"><b>Method.</b> Every fight is a clean arena duel on an empty map: both sides
spawn as leash-less attack-closest units (no horde AI, no routing/morale — both sides forced
fearless), melee swings resolve instantly (no animation windup, symmetric for both sides).
Spawn side, spawn order, and faction assignment alternate across trials to cancel systematic
biases. The search keeps armies small: first the winner's squad shrinks (5&rarr;4&rarr;3, loser
at 5), then the loser's count is bracketed and bisected at winner=3 (up to 15), winner=2 (up
to 25), and winner=1 (up to 30) until the win rate lands in 40&ndash;60%. "N+" with "can't
win" means the losing side lost every fight even at 30-to-1.</p>
<h2>All measured pairs (table view)</h2>
<table class="detail">
<tr><th>Unit A</th><th>Unit B</th><th>~50% counts</th><th>A win rate</th>
<th>W-L-D</th><th>avg fight</th><th>winner survivors</th><th>resolution</th></tr>
{''.join(detail_rows)}
</table>
<div id="tip"></div>
<script>
const tip = document.getElementById('tip');
document.querySelectorAll('[data-tip]').forEach(el => {{
  el.addEventListener('mousemove', e => {{
    tip.textContent = el.dataset.tip;
    tip.style.display = 'block';
    const pad = 14;
    let x = e.clientX + pad, y = e.clientY + pad;
    const r = tip.getBoundingClientRect();
    if (x + r.width > innerWidth - 8) x = e.clientX - r.width - pad;
    if (y + r.height > innerHeight - 8) y = e.clientY - r.height - pad;
    tip.style.left = x + 'px'; tip.style.top = y + 'px';
  }});
  el.addEventListener('mouseleave', () => tip.style.display = 'none');
}});
</script>
</body></html>
"""

with open(OUT, "w", encoding="utf-8") as f:
    f.write(html)
print(f"wrote {OUT} ({done}/{total_expected} pairs)")
