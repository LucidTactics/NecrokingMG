import re
from collections import defaultdict

phases = defaultdict(lambda: defaultdict(list))
with open('bin/Publish/log/perf.log') as f:
    for line in f:
        m = re.search(r'u=(\d+)', line)
        if not m: continue
        u = int(m.group(1))
        t = re.search(r't=([\d.]+)ms', line)
        if t: phases[u]['t'].append(float(t.group(1)))
        for name in ['movement','ai','ai_archetype','ai_awareness','horde_states','quadtree','pathfinder','physics','combat','facing']:
            mm = re.search(rf'\b{name}=([\d.]+)', line)
            if mm: phases[u][name].append(float(mm.group(1)))
        dj = re.search(r'dj_ms:([\d.]+)', line)
        if dj: phases[u]['dj_ms'].append(float(dj.group(1)))
        pend = re.search(r'pend:(\d+)', line)
        if pend: phases[u]['pend'].append(int(pend.group(1)))
        stale = re.search(r'stale:(\d+)', line)
        if stale: phases[u]['stale'].append(int(stale.group(1)))

def avg(xs):
    return sum(xs)/len(xs) if xs else 0.0

print(f"{'u':>4} {'n':>5} {'avg_t':>6} {'mov':>5} {'ai':>5} {'arch':>5} {'aware':>5} {'hstate':>6} {'pf':>5} {'dj':>5} {'pend':>5} {'stale':>5}")
for u in sorted(phases.keys()):
    p = phases[u]
    n = len(p['t'])
    if n < 30: continue
    print(f"{u:4d} {n:5d} {avg(p['t']):6.2f} {avg(p['movement']):5.2f} {avg(p['ai']):5.2f} {avg(p['ai_archetype']):5.2f} {avg(p['ai_awareness']):5.2f} {avg(p['horde_states']):6.2f} {avg(p['pathfinder']):5.2f} {avg(p['dj_ms']):5.2f} {avg(p['pend']):5.1f} {avg(p['stale']):5.1f}")
