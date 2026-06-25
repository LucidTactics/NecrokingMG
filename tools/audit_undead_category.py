#!/usr/bin/env python3
"""Audit: undead, non-player units whose undeadCategory is 'None'.

HordeCapTracker counts a unit toward a cap (Monster/Human) only if its def's
undeadCategory matches. An undead horde minion with category 'None' is invisible
to the count AND bypasses the cap — almost always a data oversight (e.g. ZombieRat).

Player forms (necromancer evolutions) are legitimately 'None' (they don't count),
so they're listed separately, not flagged.
"""
import json
import os

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
with open(os.path.join(REPO, "data", "units.json"), encoding="utf-8") as f:
    doc = json.load(f)
units = doc if isinstance(doc, list) else next(v for v in doc.values() if isinstance(v, list))

flagged, player_forms = [], []
for u in units:
    if u.get("faction") != "Undead":
        continue
    if u.get("undeadCategory", "None") != "None":
        continue
    (player_forms if u.get("playerForm") else flagged).append(u.get("id"))

print(f"Undead minions with undeadCategory=None (suspicious — uncounted/uncapped): {len(flagged)}")
for i in sorted(flagged):
    print(f"  {i}")
print(f"\nPlayer forms with None (expected, not flagged): {len(player_forms)}")
for i in sorted(player_forms):
    print(f"  {i}")
