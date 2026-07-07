#!/usr/bin/env python3
"""tools/label_store.py -- query and maintain the duplication-review label store.

Store lives in docs/consolidation-review/store/:
  labels.json    one entry per method/ctor (facets + body12 fingerprint), one per line
  verdicts.json  the judged findings, anchored to store keys
  meta.json      schema/date/commit/counts + the hash definitions
  taxonomy.md    the labeler contract (verbs/targets)

stdlib-only. Design + rationale: docs/consolidation-review/operationalizing.md.

Identity (must match store/meta.json AND tools/MethodExtractor/Program.cs):
  key    = file::type::name::sha1(sig_no_whitespace)[:8]   (+'@line' on collision)
  body12 = sha1( body, block+line comments removed, ALL whitespace removed )[:12]

Verbs:
  query      score store entries against facets / an exemplar / free text
  diff       bucket the store against a fresh extractor catalog (unchanged/changed/moved/new/deleted)
  relink     transfer labels + verdict anchors across renames/moves (by body12); prune deleted; refresh lines
  add        insert/update one entry (resolve sig/type/body12 from a catalog when available)
  import     merge *.labels.json batch outputs into the store; validate coverage; update meta
  reconcile  apply the carry-forward / re-judge rules to verdicts.json vs current body12s
  status     one-paragraph store health vs a fresh catalog
"""
import argparse
import glob
import hashlib
import json
import os
import re
import subprocess
import sys
from collections import Counter, defaultdict

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
STORE = os.path.join(REPO, "docs", "consolidation-review", "store")
LABELS_PATH = os.path.join(STORE, "labels.json")
VERDICTS_PATH = os.path.join(STORE, "verdicts.json")
META_PATH = os.path.join(STORE, "meta.json")
DEFAULT_EXTRACT = os.path.join(REPO, "cache", "method_extract")
RUN_EXTRACTOR = os.path.join(REPO, "tools", "run_method_extractor.py")

# ---------------------------------------------------------------------------
# hashing -- identical to store/meta.json spec and MethodExtractor BodyHash/Key
# ---------------------------------------------------------------------------

def sig8(sig: str) -> str:
    return hashlib.sha1(re.sub(r"\s+", "", sig).encode("utf-8")).hexdigest()[:8]


def body12(body: str) -> str:
    s = re.sub(r"/\*.*?\*/", "", body, flags=re.S)   # block comments
    s = re.sub(r"//[^\n]*", "", s)                    # line + /// doc comments
    s = re.sub(r"\s+", "", s)                          # all whitespace
    return hashlib.sha1(s.encode("utf-8")).hexdigest()[:12]


def compose_key(file: str, type_: str, name: str, sig: str) -> str:
    return f"{file}::{type_}::{name}::{sig8(sig)}"


# ---------------------------------------------------------------------------
# store I/O -- exact on-disk formats (verified against the committed files)
# ---------------------------------------------------------------------------

def load_labels():
    with open(LABELS_PATH, encoding="utf-8") as f:
        return json.load(f)


def save_labels(entries):
    """One JSON object per line, sorted by (file, line), matching the persisted format."""
    entries = sorted(entries, key=lambda e: (e.get("file", ""), e.get("line") or 0))
    body = ",\n".join(json.dumps(e, ensure_ascii=False) for e in entries)
    with open(LABELS_PATH, "w", encoding="utf-8") as f:
        f.write("[\n" + body + "\n]\n")


def load_verdicts():
    with open(VERDICTS_PATH, encoding="utf-8") as f:
        return json.load(f)


def save_verdicts(v):
    with open(VERDICTS_PATH, "w", encoding="utf-8") as f:
        f.write(json.dumps(v, ensure_ascii=False, indent=1))  # no trailing newline


def load_meta():
    with open(META_PATH, encoding="utf-8") as f:
        return json.load(f)


def save_meta(m):
    with open(META_PATH, "w", encoding="utf-8") as f:
        f.write(json.dumps(m, ensure_ascii=False, indent=2))


# ---------------------------------------------------------------------------
# catalog (fresh extractor output)
# ---------------------------------------------------------------------------

def ensure_catalog(extract_dir, run=False):
    catp = os.path.join(extract_dir, "catalog.json")
    if run or not os.path.exists(catp):
        print(f"[label_store] running MethodExtractor -> {extract_dir} ...", file=sys.stderr)
        r = subprocess.run([sys.executable, RUN_EXTRACTOR, extract_dir],
                           capture_output=True, text=True)
        sys.stderr.write(r.stdout)
        if r.returncode != 0:
            sys.stderr.write(r.stderr)
            sys.exit("[label_store] extractor failed; build it first: "
                     "dotnet build tools/MethodExtractor/MethodExtractor.csproj")
    return catp


def load_catalog(extract_dir):
    """Return {key: entry}. Prefers the extractor's BodyHash/Key; falls back to
    computing body12 from the batch bodies (non-scenario only) for older catalogs."""
    catp = os.path.join(extract_dir, "catalog.json")
    cat = json.load(open(catp, encoding="utf-8"))
    need_bodies = any("BodyHash" not in r for r in cat)
    bodies = {}
    if need_bodies:
        for bf in glob.glob(os.path.join(extract_dir, "batches", "*.json")):
            for r in json.load(open(bf, encoding="utf-8")):
                bodies[r["Id"]] = r["Body"]
    out = {}
    seen = set()
    for r in cat:
        file, type_, name, sig = r["File"], r["Type"], r["Name"], r["Sig"]
        bh = r.get("BodyHash")
        if bh is None:
            b = bodies.get(r["Id"])
            bh = body12(b) if b is not None else ""  # scenario bodies absent from batches
        key = r.get("Key") or compose_key(file, type_, name, sig)
        if key in seen:
            key = f"{key}@{r.get('StartLine')}"
        seen.add(key)
        out[key] = {
            "key": key, "file": file, "type": type_, "name": name,
            "kind": r.get("Kind", "method"), "sig": sig,
            "line": r.get("StartLine"), "lines": r.get("Lines"),
            "body12": bh, "id": r.get("Id"),
        }
    return out


# ---------------------------------------------------------------------------
# diff / bucketing (shared by diff, status, relink, reconcile)
# ---------------------------------------------------------------------------

def compute_buckets(store_by_key, cat_by_key):
    sk, ck = set(store_by_key), set(cat_by_key)
    common = sk & ck
    unchanged, changed, nofp = [], [], []
    for k in common:
        sb, cb = store_by_key[k].get("body12"), cat_by_key[k].get("body12")
        if not sb or not cb:
            nofp.append(k)          # scenario/auto entries have no fingerprint -> not drift
        elif sb == cb:
            unchanged.append(k)
        else:
            changed.append(k)
    new_keys = ck - sk
    del_keys = sk - ck

    # moves: a deleted store key whose body12 uniquely matches one new catalog key
    new_by_body = defaultdict(list)
    for k in new_keys:
        b = cat_by_key[k].get("body12")
        if b:
            new_by_body[b].append(k)
    del_by_body = defaultdict(list)
    for k in del_keys:
        b = store_by_key[k].get("body12")
        if b:
            del_by_body[b].append(k)
    moves = []
    for b, dks in del_by_body.items():
        nks = new_by_body.get(b, [])
        if len(dks) == 1 and len(nks) == 1:
            moves.append((dks[0], nks[0]))
    moved_old = {m[0] for m in moves}
    moved_new = {m[1] for m in moves}
    new_only = [k for k in new_keys if k not in moved_new]
    del_only = [k for k in del_keys if k not in moved_old]

    total = len(ck)
    drift = (len(changed) + len(new_only)) / total * 100 if total else 0.0
    return {
        "unchanged": unchanged, "changed": changed, "nofp": nofp,
        "new": new_only, "deleted": del_only, "moves": moves,
        "total_catalog": total, "total_store": len(sk), "drift_pct": drift,
    }


# ---------------------------------------------------------------------------
# verdict anchor index
# ---------------------------------------------------------------------------

def verdict_index(verdicts):
    """key -> list of (unit, verdict, title)."""
    idx = defaultdict(list)
    for u in verdicts.get("units", []):
        for f in u.get("findings", []):
            for a in f.get("anchors", []) or []:
                idx[a].append((u["unit"], f.get("verdict", "?"), f.get("title", "")))
    return idx


# ---------------------------------------------------------------------------
# query
# ---------------------------------------------------------------------------

TARGET_SYNONYMS = [
    {"unit", "corpse", "horde", "squad"},
    {"env-object", "foragable", "prop", "decoration"},
    {"window", "panel", "widget", "button", "list", "tooltip"},
    {"texture", "sprite", "atlas", "animation"},
    {"spell", "buff", "ability"},
    {"tile", "ground", "wall", "road", "zone", "map-data"},
    {"save-file", "settings", "map-data"},
    {"item", "weapon", "armor", "potion", "shield"},
]


def _tokens(s):
    s = s or ""
    s = re.sub(r"([a-z0-9])([A-Z])", r"\1 \2", s)  # split CamelCase
    return [t for t in re.split(r"[^A-Za-z0-9]+", s.lower()) if t]


def _target_score(a, b):
    if a == b:
        return 1.0
    for grp in TARGET_SYNONYMS:
        if a in grp and b in grp:
            return 0.7
    return 0.0


def _jaccard(a, b):
    sa, sb = set(_tokens(a)), set(_tokens(b))
    if not sa or not sb:
        return 0.0
    return len(sa & sb) / len(sa | sb)


def _name_ratio(a, b):
    import difflib
    return difflib.SequenceMatcher(None, " ".join(_tokens(a)), " ".join(_tokens(b))).ratio()


def score_entry(e, q):
    s = 0.0
    if q.get("verb"):
        if e.get("verb") == q["verb"]:
            s += 3.0
        elif not q.get("any_verb"):
            return -1.0  # verb is a hard filter by default
    if q.get("target"):
        s += 2.0 * _target_score(e.get("target", ""), q["target"])
    if q.get("mechanism"):
        s += 1.5 * _jaccard(e.get("mechanism", ""), q["mechanism"])
    if q.get("like_name"):
        s += 2.0 * _name_ratio(e.get("name", ""), q["like_name"])
    if q.get("text"):
        blob = " ".join(str(e.get(k, "")) for k in ("summary", "mechanism", "name", "dup_hint"))
        s += 1.5 * _jaccard(blob, q["text"])
    return s


def find_probe(entries, like):
    """Resolve --like as an exact key, or file::...::name, or file:name / Type.Name."""
    by_key = {e["key"]: e for e in entries}
    if like in by_key:
        return by_key[like]
    for e in entries:
        if e["key"].startswith(like):
            return e
    if "::" in like:
        parts = like.split("::")
        name = parts[-1]
        cands = [e for e in entries if e["name"] == name and (len(parts) < 2 or e["file"] == parts[0])]
        if cands:
            return cands[0]
    if ":" in like:  # file:name
        f, nm = like.rsplit(":", 1)
        cands = [e for e in entries if e["name"] == nm and (e["file"] == f or e["file"].endswith(f))]
        if cands:
            return cands[0]
    if "." in like:  # Type.Name
        ty, nm = like.rsplit(".", 1)
        cands = [e for e in entries if e["name"] == nm and e["type"].split(".")[-1] == ty]
        if cands:
            return cands[0]
    return None


def cmd_query(args):
    entries = load_labels()
    vidx = verdict_index(load_verdicts())
    q = {"verb": args.verb, "target": args.target, "mechanism": args.mechanism,
         "like_name": args.like_name, "text": args.text, "any_verb": args.any_verb}
    probe = None
    if args.like:
        probe = find_probe(entries, args.like)
        if not probe:
            sys.exit(f"[query] could not resolve exemplar --like {args.like!r}")
        q = {"verb": probe["verb"], "target": probe["target"],
             "mechanism": probe["mechanism"], "like_name": probe["name"],
             "text": probe.get("summary", ""), "any_verb": args.any_verb}
        print(f"# exemplar: {probe['key']}  [{probe['verb']}|{probe['target']}] {probe.get('summary','')}")
    if not any(q.get(k) for k in ("verb", "target", "mechanism", "like_name", "text")):
        sys.exit("[query] give at least one of --verb/--target/--mechanism/--like-name/--text or --like")

    scored = []
    for e in entries:
        if probe is not None and e["key"] == probe["key"]:
            continue
        sc = score_entry(e, q)
        if sc > 0:
            scored.append((sc, e))
    scored.sort(key=lambda t: -t[0])
    for sc, e in scored[: args.top]:
        flag = ""
        if e["key"] in vidx:
            v = vidx[e["key"]][0]
            flag = f"  <{v[1]}: {v[2][:60]}>"
        print(f"{sc:5.2f}  {e['file']}:{e.get('line')}  {e['type']}.{e['name']} "
              f"({e.get('lines')}L) [{e.get('verb')}|{e.get('target')}] {e.get('summary','')}{flag}")
    if not scored:
        print("# no matches")


# ---------------------------------------------------------------------------
# diff / status
# ---------------------------------------------------------------------------

def cmd_diff(args):
    extract_dir = args.extract_dir or DEFAULT_EXTRACT
    ensure_catalog(extract_dir, run=args.run_extractor)
    store = {e["key"]: e for e in load_labels()}
    cat = load_catalog(extract_dir)
    b = compute_buckets(store, cat)
    print(f"catalog={b['total_catalog']}  store={b['total_store']}  "
          f"drift={b['drift_pct']:.1f}%")
    print(f"  unchanged : {len(b['unchanged'])}")
    print(f"  changed   : {len(b['changed'])}")
    print(f"  new       : {len(b['new'])}")
    print(f"  deleted   : {len(b['deleted'])}")
    print(f"  moved     : {len(b['moves'])}  (relink transfers these, 0 LLM cost)")
    print(f"  no-fingerprint (auto/scenario, not drift) : {len(b['nofp'])}")
    if args.show:
        for label in ("changed", "new", "deleted"):
            for k in sorted(b[label])[: args.show]:
                print(f"  [{label}] {k}")
        for old, new in b["moves"][: args.show]:
            print(f"  [moved] {old}\n       -> {new}")
    if args.json:
        with open(args.json, "w", encoding="utf-8") as f:
            json.dump(b, f, indent=1)
        print(f"# wrote worklist -> {args.json}")


def cmd_status(args):
    extract_dir = args.extract_dir or DEFAULT_EXTRACT
    meta = load_meta()
    entries = load_labels()
    verdicts = load_verdicts()
    n_find = sum(len(u["findings"]) for u in verdicts["units"])
    n_unanch = sum(1 for u in verdicts["units"] for f in u["findings"] if not f.get("anchors"))
    have_cat = os.path.exists(os.path.join(extract_dir, "catalog.json")) or args.run_extractor
    line = (f"store: {len(entries)} entries "
            f"({meta['counts'].get('labeled','?')} labeled + "
            f"{meta['counts'].get('auto_scenario','?')} auto-scenario), "
            f"labeled at {meta.get('persisted_at_commit','?')[:8]} on "
            f"{meta.get('labeled_date','?')}; "
            f"{n_find} verdict findings ({n_unanch} unanchored).")
    if have_cat:
        ensure_catalog(extract_dir, run=args.run_extractor)
        cat = load_catalog(extract_dir)
        b = compute_buckets({e["key"]: e for e in entries}, cat)
        line += (f" vs HEAD catalog ({b['total_catalog']} methods): "
                 f"drift {b['drift_pct']:.1f}% "
                 f"({len(b['changed'])} changed, {len(b['new'])} new, "
                 f"{len(b['deleted'])} deleted, {len(b['moves'])} moved).")
        if b["drift_pct"] < 5:
            line += " Drift <5%: a full /dup-review is probably not worth it yet."
    else:
        line += " (No catalog found; pass --run-extractor for staleness.)"
    print(line)


# ---------------------------------------------------------------------------
# relink
# ---------------------------------------------------------------------------

def cmd_relink(args):
    extract_dir = args.extract_dir or DEFAULT_EXTRACT
    ensure_catalog(extract_dir, run=args.run_extractor)
    entries = load_labels()
    store = {e["key"]: e for e in entries}
    cat = load_catalog(extract_dir)
    b = compute_buckets(store, cat)
    remap = dict(b["moves"])  # old_key -> new_key

    # apply moves + prune deleted + refresh line numbers
    kept = []
    for e in entries:
        k = e["key"]
        if k in remap:
            nk = remap[k]
            c = cat[nk]
            e = dict(e)
            e.update(key=nk, file=c["file"], type=c["type"], name=c["name"],
                     sig=c["sig"], line=c["line"], lines=c["lines"])
            kept.append(e)
        elif k in store and k in cat:
            c = cat[k]
            if e.get("line") != c["line"] or e.get("lines") != c["lines"]:
                e = dict(e)
                e["line"], e["lines"] = c["line"], c["lines"]
            kept.append(e)
        else:
            # deleted (and not a move) -> pruned
            continue

    # remap verdict anchors old->new
    verdicts = load_verdicts()
    changed_v = 0
    for u in verdicts["units"]:
        for f in u["findings"]:
            if f.get("anchors"):
                f["anchors"] = [remap.get(a, a) for a in f["anchors"]]
            ab = f.get("anchor_body12")
            if ab:
                f["anchor_body12"] = {remap.get(a, a): h for a, h in ab.items()}
            if any(a in remap for a in (f.get("anchors") or [])):
                changed_v += 1

    pruned = len(entries) - len([e for e in entries if e["key"] in cat or e["key"] in remap])
    print(f"relink: {len(remap)} moved, {len(b['deleted'])} pruned, "
          f"lines refreshed; verdict anchors remapped in {changed_v} finding(s).")
    if args.dry_run:
        print("# --dry-run: no files written")
        return
    save_labels(kept)
    save_verdicts(verdicts)
    print(f"# wrote {LABELS_PATH} and {VERDICTS_PATH}")


# ---------------------------------------------------------------------------
# add
# ---------------------------------------------------------------------------

def cmd_add(args):
    entries = load_labels()
    by_key = {e["key"]: e for e in entries}

    file_, type_, name, sig = args.file, args.type, args.name, args.sig
    line = args.line
    lines = args.lines
    bh = args.body12
    kind = args.kind

    # resolve from a fresh catalog when possible (fills type/sig/line/body12)
    if not (type_ and sig) or bh is None:
        extract_dir = args.extract_dir or DEFAULT_EXTRACT
        if os.path.exists(os.path.join(extract_dir, "catalog.json")):
            cat = load_catalog(extract_dir)
            cands = [c for c in cat.values() if c["file"] == file_ and c["name"] == name
                     and (not type_ or c["type"] == type_)]
            if len(cands) > 1:
                sys.exit("[add] ambiguous: multiple methods match; pass --type/--sig.\n  " +
                         "\n  ".join(c["key"] for c in cands))
            if cands:
                c = cands[0]
                type_ = type_ or c["type"]
                sig = sig or c["sig"]
                line = line if line is not None else c["line"]
                lines = lines if lines is not None else c["lines"]
                bh = bh if bh is not None else c["body12"]
                kind = kind or c["kind"]

    if not (type_ and sig):
        sys.exit("[add] need --type and --sig (no catalog match to resolve them). "
                 "Build the extractor and run it, or pass them explicitly.")
    key = args.key or compose_key(file_, type_, name, sig)
    entry = {
        "key": key, "file": file_, "type": type_, "name": name,
        "kind": kind or "method", "sig": sig, "line": line, "lines": lines,
        "body12": bh or "",
        "verb": args.verb, "target": args.target, "mechanism": args.mechanism or "",
        "summary": args.summary or "", "dup_hint": args.dup_hint or "",
    }
    action = "updated" if key in by_key else "added"
    by_key[key] = entry
    save_labels(list(by_key.values()))
    print(f"{action}: {key}")
    print(f"  [{entry['verb']}|{entry['target']}] {entry['summary']}")


# ---------------------------------------------------------------------------
# import
# ---------------------------------------------------------------------------

def cmd_import(args):
    extract_dir = args.extract_dir or DEFAULT_EXTRACT
    ensure_catalog(extract_dir, run=args.run_extractor)
    cat_by_key = load_catalog(extract_dir)
    cat_by_id = {c["id"]: c for c in cat_by_key.values() if c.get("id") is not None}

    # which ids were sent for labeling (batches) -> to validate coverage
    batch_ids = set()
    for bf in glob.glob(os.path.join(extract_dir, "batches", "*.json")):
        for r in json.load(open(bf, encoding="utf-8")):
            batch_ids.add(r["Id"])

    labels_dir = args.labels_dir
    label_by_id = {}
    dupes = []
    bad = []
    for f in sorted(glob.glob(os.path.join(labels_dir, "*.labels.json"))):
        try:
            arr = json.load(open(f, encoding="utf-8"))
        except Exception as e:
            bad.append((os.path.basename(f), str(e)))
            continue
        for lab in arr:
            if not isinstance(lab, dict) or "id" not in lab:
                continue
            if lab["id"] in label_by_id:
                dupes.append(lab["id"])
            label_by_id[lab["id"]] = lab

    entries = load_labels()
    by_key = {e["key"]: e for e in entries}
    imported = 0
    missing_cat = []
    for mid, lab in label_by_id.items():
        c = cat_by_id.get(mid)
        if not c:
            missing_cat.append(mid)
            continue
        entry = {
            "key": c["key"], "file": c["file"], "type": c["type"], "name": c["name"],
            "kind": c["kind"], "sig": c["sig"], "line": c["line"], "lines": c["lines"],
            "body12": c["body12"],
            "verb": (lab.get("verb") or "other").strip(),
            "target": (lab.get("target") or "misc").strip(),
            "mechanism": (lab.get("mechanism") or "").strip(),
            "summary": (lab.get("summary") or "").strip(),
            "dup_hint": (lab.get("dup_hint") or "").strip(),
        }
        by_key[c["key"]] = entry
        imported += 1

    answered = set(label_by_id)
    unanswered = sorted(batch_ids - answered) if batch_ids else []
    print(f"import: {imported} entries merged from {labels_dir}")
    print(f"  label files bad={bad}  duplicate ids={len(dupes)}")
    if batch_ids:
        print(f"  coverage: {len(answered & batch_ids)}/{len(batch_ids)} batch ids answered; "
              f"{len(unanswered)} unanswered")
        if unanswered:
            print("  unanswered sample: " + ", ".join(map(str, unanswered[:20])))
    if missing_cat:
        print(f"  WARNING: {len(missing_cat)} labeled ids not in catalog (stale batches?)")
    if args.dry_run:
        print("# --dry-run: store not written")
        return
    save_labels(list(by_key.values()))
    meta = load_meta()
    all_entries = list(by_key.values())
    meta.setdefault("counts", {})["total"] = len(all_entries)
    meta["counts"]["labeled"] = sum(1 for e in all_entries if e.get("body12"))
    meta["counts"]["auto_scenario"] = sum(1 for e in all_entries if not e.get("body12"))
    meta["bad_label_files"] = [b[0] for b in bad]
    save_meta(meta)
    print(f"# wrote {LABELS_PATH}; meta counts refreshed (total={meta['counts']['total']})")


# ---------------------------------------------------------------------------
# reconcile
# ---------------------------------------------------------------------------

def cmd_reconcile(args):
    extract_dir = args.extract_dir or DEFAULT_EXTRACT
    ensure_catalog(extract_dir, run=args.run_extractor)
    cat = load_catalog(extract_dir)
    cur_body = {k: v.get("body12") for k, v in cat.items()}
    verdicts = load_verdicts()

    worklist = {"carried": [], "resolved": [], "needs_rejudge": [], "needs_review": []}
    for u in verdicts["units"]:
        for f in u["findings"]:
            anchors = f.get("anchors") or []
            ab = f.get("anchor_body12") or {}
            entry = {"unit": u["unit"], "verdict": f.get("verdict"),
                     "title": f.get("title", ""), "anchors": anchors}
            if not anchors:
                worklist["needs_review"].append(entry)  # prose-only, always re-checked as prior context
                continue
            deleted = [a for a in anchors if a not in cur_body]
            drifted = [a for a in anchors if a in cur_body and ab.get(a) and cur_body[a] != ab[a]]
            entry["deleted"], entry["drifted"] = deleted, drifted
            v = (f.get("verdict") or "").upper()
            if v == "CONSOLIDATE":
                # reconcile against reality: duplicate gone (anchor deleted) => resolved
                if deleted:
                    worklist["resolved"].append(entry)
                else:
                    worklist["needs_review"].append(entry)  # still present -> re-surface as-is
            else:  # KEEP_SEPARATE / INVESTIGATE
                if not deleted and not drifted:
                    worklist["carried"].append(entry)       # unchanged -> carry verbatim, no judge
                else:
                    worklist["needs_rejudge"].append(entry)

    print(f"reconcile against {os.path.relpath(extract_dir, REPO)} "
          f"({len(cat)} methods):")
    for bucket in ("carried", "resolved", "needs_rejudge", "needs_review"):
        items = worklist[bucket]
        print(f"  {bucket:14s}: {len(items)}")
    if args.show:
        for bucket in ("needs_rejudge", "resolved"):
            for e in worklist[bucket]:
                extra = ""
                if e.get("drifted"):
                    extra += f" drifted={len(e['drifted'])}"
                if e.get("deleted"):
                    extra += f" deleted={len(e['deleted'])}"
                print(f"  [{bucket}] {e['unit']} :: {e['verdict']} :: {e['title'][:70]}{extra}")
    if args.json:
        with open(args.json, "w", encoding="utf-8") as f:
            json.dump(worklist, f, indent=1)
        print(f"# wrote judge worklist -> {args.json}")


# ---------------------------------------------------------------------------
# cli
# ---------------------------------------------------------------------------

def main():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)

    def add_extract_opts(sp):
        sp.add_argument("--extract-dir", help="extractor output dir (default cache/method_extract)")
        sp.add_argument("--run-extractor", action="store_true", help="regenerate the catalog first")

    q = sub.add_parser("query", help="score store entries against facets/exemplar/text")
    q.add_argument("--verb")
    q.add_argument("--target")
    q.add_argument("--mechanism")
    q.add_argument("--like-name", dest="like_name")
    q.add_argument("--text")
    q.add_argument("--like", help="exemplar: a store key, file::type::name, file:name, or Type.Name")
    q.add_argument("--any-verb", action="store_true", help="relax the verb hard-filter")
    q.add_argument("--top", type=int, default=12)
    q.set_defaults(func=cmd_query)

    d = sub.add_parser("diff", help="bucket the store vs a fresh catalog")
    add_extract_opts(d)
    d.add_argument("--show", type=int, default=0, help="print up to N keys per bucket")
    d.add_argument("--json", help="write the bucket worklist to this path")
    d.set_defaults(func=cmd_diff)

    s = sub.add_parser("status", help="one-paragraph store health")
    add_extract_opts(s)
    s.set_defaults(func=cmd_status)

    r = sub.add_parser("relink", help="transfer labels+anchors across moves; prune deleted; refresh lines")
    add_extract_opts(r)
    r.add_argument("--dry-run", action="store_true")
    r.set_defaults(func=cmd_relink)

    a = sub.add_parser("add", help="insert/update one entry")
    a.add_argument("--file", required=True)
    a.add_argument("--name", required=True)
    a.add_argument("--verb", required=True)
    a.add_argument("--target", required=True)
    a.add_argument("--type")
    a.add_argument("--sig")
    a.add_argument("--kind")
    a.add_argument("--line", type=int)
    a.add_argument("--lines", type=int)
    a.add_argument("--body12")
    a.add_argument("--key")
    a.add_argument("--mechanism")
    a.add_argument("--summary")
    a.add_argument("--dup-hint", dest="dup_hint")
    a.add_argument("--extract-dir")
    a.set_defaults(func=cmd_add)

    i = sub.add_parser("import", help="merge *.labels.json batch outputs into the store")
    i.add_argument("labels_dir", help="dir containing *.labels.json")
    add_extract_opts(i)
    i.add_argument("--dry-run", action="store_true")
    i.set_defaults(func=cmd_import)

    rc = sub.add_parser("reconcile", help="apply carry-forward/re-judge rules to verdicts")
    add_extract_opts(rc)
    rc.add_argument("--show", action="store_true", help="list re-judge/resolved findings")
    rc.add_argument("--json", help="write the judge worklist to this path")
    rc.set_defaults(func=cmd_reconcile)

    args = p.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
