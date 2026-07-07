---
name: dup-review
description: Re-run the whole-codebase semantic duplication / consolidation review (label every method by intent, cluster near-duplicates, judge each unit, publish verdicts). Run ONLY when the user explicitly asks to re-run the duplication / consolidation review (or a labels-only "refresh the dup-review labels"). NEVER trigger from routine coding work — it is a multi-hour, multi-million-token operation. For an authoring-time "does similar code already exist?" check, use `tools/label_store.py query` (or the locate-behavior finder), NOT this skill.
---

# Duplication / consolidation review (re-runnable)

This skill re-runs the 2026-07-06 semantic-duplication review incrementally: it re-labels
only the methods whose bodies drifted, re-judges only the units whose evidence changed, and
carries forward everything the last review already settled. The expensive investment (the
label store + judged verdicts) lives in `docs/consolidation-review/store/`; this pipeline
updates it in place.

**Design + rationale:** `docs/consolidation-review/operationalizing.md` (read it if anything
below is ambiguous). **Store contract:** `docs/consolidation-review/store/meta.json`.

> User-invoked only. If you reached this skill from anything other than the user explicitly
> asking to re-run / refresh the duplication review, stop — this is not the tool for
> ad-hoc "where does this go?" or "is this a dup?" questions.

## The store (what persists between runs)

`docs/consolidation-review/store/` (committed, git-shared with the collaborator):
- `taxonomy.md` — the 43-verb facet taxonomy. **This is the labeler contract**; changing it
  invalidates existing labels (see Taxonomy evolution). Do not edit it outside a run.
- `labels.json` — one entry per method/ctor: stable `key`, `body12` fingerprint, and the
  `verb/target/mechanism/summary/dup_hint` facets. ~1.8 MB; query it via `label_store.py`,
  never Read it whole.
- `verdicts.json` — the judged findings, anchored to method keys (+ `anchor_body12` snapshots
  so a re-run can tell whether the ruled-on code changed).
- `meta.json` — schema/date/`persisted_at_commit`/counts + the hash definitions.

Identity (enforced by `tools/MethodExtractor` and `tools/label_store.py`, spec in meta.json):
- `key = file::type::name::sha1(sig_no_whitespace)[:8]` (+`@line` on collision)
- `body12 = sha1(body, block+line comments stripped, all whitespace stripped)[:12]`

## Model tiers

| Work | Model | Why |
|---|---|---|
| Labeling drifted/new methods (step 4) | **Sonnet** subagents, one per batch | Facet labeling is high-volume, mechanical-ish judgment; Sonnet is the cost/quality sweet spot. |
| Concept audit (step 7) | main session, **Fable** | Cross clusters against the subsystem inventory; needs whole-codebase context. |
| Unit judging (step 9) | **Fable** subagents, one per unit | Reads real code at HEAD and rules; the expensive "does the evidence dissolve on inspection?" call. |
| Everything else (steps 0–3, 5, 6, 8, 10) | main session + `label_store.py` | Zero-LLM mechanical steps. |

## Pipeline

Extract → diff vs store → relink → **label only drifted/new** → import → cluster → audit →
reconcile → **judge only changed units** → publish. Run `tools/*` via
`C:/Users/Raymo/Tools/python-3.11-embed/python.exe`.

| Step | What | Command / who |
|---|---|---|
| 0 | **Preconditions.** Confirm `dotnet build Necroking/Necroking.csproj` passes and the tree is clean-ish; warn the user if there is uncommitted churn (labels would describe a moving target). Record `git rev-parse HEAD` for meta.json. | main session |
| 1 | **Extract.** `dotnet build tools/MethodExtractor/MethodExtractor.csproj` then `python tools/run_method_extractor.py cache/method_extract`. Emits catalog (with BodyHash/Key) + labeling batches. ~1 min. | main session |
| 2 | **Diff vs store.** `python tools/label_store.py diff --extract-dir cache/method_extract --json cache/method_extract/diff.json`. Prints unchanged/changed/moved/new/deleted + drift %. **If drift < 5%, tell the user a full review probably isn't worth it yet and ask before proceeding.** | `label_store.py diff` |
| 3 | **Relink + prune.** `python tools/label_store.py relink --extract-dir cache/method_extract`. Transfers labels + verdict anchors across renames/moves (unique body12), prunes deleted, refreshes advisory line numbers. Zero LLM cost. | `label_store.py relink` |
| 4 | **Label the drift.** Only the `changed` + `new` method ids from step 2. Spawn **Sonnet** subagents, one per affected batch (`cache/method_extract/batches/batch_NNN.json`), each given `labeler-prompt.md`. Each writes `batch_NNN.labels.json`. Typical re-run: 3–8 batches; from scratch: ~27. | Sonnet agents |
| 5 | **Import labels.** `python tools/label_store.py import <labels-dir> --extract-dir cache/method_extract`. Validates every batch id was answered; upserts entries; refreshes meta counts. | `label_store.py import` |
| 6 | **Cluster.** `python tools/cluster_labels.py --store --extract-dir cache/method_extract --out cache/method_extract/clusters`. Seconds. | `cluster_labels.py --store` |
| 7 | **Concept audit.** Cross `clusters/summary.txt` + labeler `dup_hint`s against the subsystem inventory in `docs/locate-behavior/overview.md` → 15–25 investigation units. **Reuse the previous unit list** (`verdicts.json` `units[]`) as the frame; add units only for genuinely new/changed concept areas. | main session (Fable) |
| 8 | **Reconcile.** `python tools/label_store.py reconcile --extract-dir cache/method_extract --show --json cache/method_extract/judge_worklist.json`. Emits per-finding buckets: `carried` / `resolved` / `needs_rejudge` / `needs_review` (rules below). | `label_store.py reconcile` |
| 9 | **Judge.** For each unit with anything in `needs_rejudge` / `needs_review` (or new cluster evidence), spawn a **Fable** subagent with `judge-prompt.md` + the unit's cluster evidence + its prior dossier + prior verdicts. Each **must read the actual code at HEAD** and **emit anchors (store keys) for every finding**. Units that are entirely `carried` get NO judge. | Fable agents |
| 10 | **Publish.** Rewrite `docs/consolidation-review/verdicts.md` + changed `dossiers/*`; update `store/verdicts.json` + `store/meta.json` (new `persisted_at_commit`, date, counts); append actionable CONSOLIDATE items to `memory/consolidation_opportunities.md`; refresh the README date/counts. Then **offer the user a single commit** (per git policy — this skill makes no commits or pushes itself). | main session |

## Reconciliation rules (step 8, from operationalizing.md §1.3)

A re-run's #1 waste is re-litigating settled KEEP_SEPARATE rulings. `reconcile` prevents it:

- **KEEP_SEPARATE / INVESTIGATE, all anchors unchanged** (`body12` matches `anchor_body12`)
  → **carried** verbatim, no judge spawned.
- **KEEP_SEPARATE / INVESTIGATE with any anchor changed or deleted** → **needs_rejudge**;
  the judge gets the prior ruling quoted and must state specifically what changed in the
  code to overturn it.
- **CONSOLIDATE with an anchor now deleted** (the duplicate got consolidated) → **resolved**;
  mark it done, no judge.
- **CONSOLIDATE still fully present** → **needs_review** (re-surfaced as-is; the code
  evidence is already in the dossier — usually no full re-judge needed).
- **Unanchored findings** (prose named no resolvable method) → **needs_review**: always handed
  to the unit's judge as prior context, never silently re-litigated. The judge confirms or
  overturns explicitly and **names anchor methods** so the next run can auto-carry them.

KEEP_SEPARATE persists unless the anchored code drifted. That is the whole point of the store.

## Budget expectations (state these so the user can abort)

- **Typical re-run** (store valid, 10–30% drift): labeling 0.3–0.8M (Sonnet) + judging only
  changed/new units 0.5–1M (Fable) + audit/publish ≈ **1.5–2.5M tokens, ~1–2 h**.
- **From scratch** (no usable store, or taxonomy changed): ≈ 2.6M Sonnet + ~0.3M audit +
  ~1.8M Fable ≈ **4.5–5M tokens, several hours**.
- **Labels-only refresh** ("refresh the dup-review labels", steps 1–6 only, no audit/judging):
  ≈ 0.5M Sonnet at ~20% drift.
- Hard rule: if `diff` reports **< 5% drift**, a full review probably isn't worth it — ask
  the user before proceeding.

## Taxonomy evolution

`store/taxonomy.md` may change **only as part of a run**, because labels are comparable only
within one taxonomy version. Additive (new verb) → old labels stay valid. Rename/split →
map old verbs forward during `import` and bump `schema` in meta.json. A structural rewrite
forces a full re-label (the ~2.6M worst case) — that's why the bar for taxonomy edits is high.

## Output locations

- Store updates: `docs/consolidation-review/store/{labels.json, verdicts.json, meta.json}`.
- Human-facing: `docs/consolidation-review/{README.md, verdicts.md, dossiers/*}` (rewritten
  in place; git history is the archive of past reviews).
- Actionable follow-ups: appended to `memory/consolidation_opportunities.md`.
- Regenerable scratch (gitignored): `cache/method_extract/` (catalog, batches, clusters,
  worklists).

## Worked example — the 2026-07-06 run

The prior run is the reference artifact set: `docs/consolidation-review/verdicts.md` +
`dossiers/` are its human output; `store/verdicts.json` holds its 116 findings across 20
units (71 anchored); `store/labels.json` holds all 3,551 labeled methods. It cost ~2.6M
Sonnet (labeling) + ~1.8M Fable (judging). A re-run diffs against that baseline instead of
starting cold — which is exactly why the store is committed.

## Authoring-time fallback (NOT this skill)

Between reviews, new significant methods enter the store via
`python tools/label_store.py add ...`, and "does this already exist?" is answered by
`python tools/label_store.py query --verb V --target T --like-name foo` (or the
locate-behavior finder). Those are cheap, instant, and do not run this pipeline.
