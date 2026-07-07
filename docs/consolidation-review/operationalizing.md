# Operationalizing the Duplication Review

Design report (2026-07-07): how to store and reuse the 2026-07-06 semantic-duplication
review so that (1) the full review is re-runnable on demand and (2) new code gets checked
against the existing codebase at authoring time.

> **Status (2026-07-07): built.** The tooling below is implemented and committed —
> `tools/label_store.py` (all 7 verbs), the `MethodExtractor` BodyHash/Key emission,
> `cluster_labels.py --store`, and the `/dup-review` skill. See the checklist in §6 for
> per-item status. **Two user decisions recorded this build:**
> - **locate-behavior integration was declined** (§3 option (d)). find-similar is NOT wired
>   into the locate-behavior finder. It stays a **user-invoked skill (`/dup-review`)** plus a
>   **manual `tools/label_store.py query` CLI** (kept explicitly so find-similar remains
>   available on request). Future sessions: do not re-add the locate-behavior integration.
> - The re-runnable review is a **user-invoked skill only**, never auto-triggered.

---

## Recommendations at a glance

| Decision | Recommendation |
|---|---|
| Label store location | `docs/consolidation-review/store/` — **committed** (already persisted this session) |
| Extractor output | `cache/method_extract/` — gitignored, regenerated on demand (existing default) |
| Method identity | `file::type::name::sig-hash` key + comment/whitespace-stripped body hash (`body12`) for staleness/rename detection |
| Verdicts | Persisted in the store (`verdicts.json`, anchored to method keys) so KEEP_SEPARATE rulings are never re-litigated blind |
| Use 1 (full re-run) | New curated skill **`/dup-review`** — user-invoked only, incremental by default (re-labels only drifted methods, re-judges only units whose evidence changed) |
| Use 2 (authoring-time) | **Manual `tools/label_store.py query` CLI** (locate-behavior integration was **declined** by the user — see §3). The finder may still be asked to run a query, but nothing auto-wires it. No hooks. |
| New machinery | One new tool: `tools/label_store.py` (~300 lines, stdlib-only). Two small mods: MethodExtractor emits `BodyHash`; `cluster_labels.py` reads the store. |
| Typical costs | find-similar query ≈ 0 agent tokens / seconds · label one new file ≈ 1–3k in-session tokens · full re-run after normal drift ≈ 1.5–2.5M tokens (vs ~4.5–5M from scratch) |

### Already done (this session, pending commit by the orchestrator)

`docs/consolidation-review/store/` now contains the merged, re-keyed artifacts — the
session-temporary scratchpad copies are no longer the only home of the ~2.6M-token
labeling investment:

- **`taxonomy.md`** — the 43-verb facet taxonomy, verbatim (this file IS the labeler
  contract; changing it invalidates labels — see Freshness).
- **`labels.json`** (1.8 MB, one entry per line for clean git diffs) — all 3,551
  methods/ctors: 3,025 agent-labeled + 526 auto-labeled scenario boilerplate, 0 unlabeled.
  Each entry is self-contained: stable key, file/type/name/kind/sig, advisory line/lines,
  `body12` fingerprint, and the verb/target/mechanism/summary/dup_hint facets.
- **`verdicts.json`** — the 116 findings from `verdicts.md` in structured form (20 units,
  verdict/confidence/title/rationale), with best-effort **anchors**: store keys of the
  methods each finding ruled on, plus a snapshot of their `body12` at ruling time.
  71/116 findings resolved at least one anchor; the rest carry prose only and are handled
  by the reconciliation procedure below.
- **`meta.json`** — schema version, dates, source commit (`a112fffb`), counts, hash
  definitions, cost provenance. Note: labels were produced from the 2026-07-06 tree and
  consolidation commits are landing now, so a `diff` against HEAD will *correctly* report
  those consolidated methods as stale — that is the mechanism working, not an error.

Not persisted (regenerable from the store in seconds, or already committed elsewhere):
`clusters/joined.json`, `clusters/clusters.json`, extractor batches (regenerate via
MethodExtractor), and the dossiers/verdicts.md (already in `docs/consolidation-review/`).

---

## 1. Storage design

### 1.1 Where each artifact lives, and why

| Artifact | Location | Versioning | Rationale |
|---|---|---|---|
| Taxonomy | `docs/consolidation-review/store/taxonomy.md` | committed | It's a contract: labels are only comparable if produced against the same taxonomy. Lives next to the data it governs. |
| Label store | `docs/consolidation-review/store/labels.json` | committed | **Not mechanically regenerable** — it cost ~2.6M Sonnet tokens. The repo's committed/gitignored line is "can a fresh clone rebuild this cheaply?" (`cache/` = yes, gitignored; this = no, committed). Also shared with the collaborator via git, like the locate-behavior map. |
| Verdict store | `docs/consolidation-review/store/verdicts.json` | committed | Same expense argument (~1.8M Fable tokens), and rulings must persist to prevent re-litigation. |
| Store metadata | `docs/consolidation-review/store/meta.json` | committed | Staleness anchor: which commit/date the labels describe. |
| Extractor output (catalog + body batches) | `cache/method_extract/` | gitignored (existing `cache/` rule) | Regenerates in ~1 minute from source; bodies would bloat git for nothing. `run_method_extractor.py` already defaults here. |
| Human-facing review outputs | `docs/consolidation-review/` (README, verdicts.md, dossiers/) | committed | Already the convention. Re-runs overwrite in place; git history is the archive of past reviews (matches the `defunct/` philosophy: git history is the durable archive). |
| Pipeline prompts (labeler/judge templates) | `.claude/skills/dup-review/` | committed via gitignore un-ignore | They're procedure, not reference — skill territory per CLAUDE.md routing style. |
| Query/update CLI | `tools/label_store.py` | committed | Normal tools/ convention. |

**Alternatives considered and rejected**

- *Scratchpad / gitignored store*: the scratchpad vanishes per-session (the very problem);
  a gitignored store dies on clone and isn't shared with the collaborator.
- *`memory/`*: per-user auto-memory, not git-shared, and 1.8 MB of JSON is not memory
  material. The pointer belongs in CLAUDE.md/skills, the data in docs/.
- *Under `.claude/`*: the repo deliberately keeps writable knowledge bases **out** of
  `.claude/` (see `docs/locate-behavior/` — moved out "so the finder can self-update it
  without write prompts", and the gitignore's default-ignore of `.claude/*` to contain
  skill litter). The store must be writable by working sessions without prompts → docs/.
- *A new top-level dir (`codemap/`)*: no precedent; `docs/consolidation-review/` already
  owns this topic and keeps data next to the review it came from.

**Size/churn note**: `labels.json` is written one-entry-per-line, sorted by file/line, so
incremental updates produce small, reviewable diffs. At 1.8 MB it is well under the repo's
15 MB handling limit, but sessions should query it via `label_store.py`, not Read it whole.

### 1.2 Method identity — what survives renames and moves

Two-part identity, both already present in the persisted store:

- **Key** (primary): `file::type::name::sig8` where `sig8` = first 8 hex of SHA-1 over the
  whitespace-normalized signature. Distinguishes overloads; collision fallback appends
  `@line` (0 collisions in the current 3,551).
- **Fingerprint**: `body12` = first 12 hex of SHA-1 over the body with block/line comments
  and all whitespace stripped. Cosmetic edits (formatting, comments) do **not** change it.

| Change | Key | body12 | Handling |
|---|---|---|---|
| Body edited | same | changed | Label marked stale; facets usually still valid (intent rarely flips) — re-label on touch or at next review |
| Comment/format only | same | same | No-op |
| Renamed / moved file / moved type | changed | same | `relink` pass: unique body12 match transfers label + verdict anchors to the new key; ambiguous or tiny-body matches treated as new |
| Signature changed | changed | usually changed | Treated as new; relink still catches the (rare) body-identical case |
| Deleted | gone | gone | Pruned on import; any verdict anchored *solely* to it is marked moot |
| New method | new | new | Unlabeled until labeled (inline or at next refresh) |

Line numbers are stored but explicitly **advisory** (refreshed on every import from a
fresh catalog); nothing keys on them except the collision fallback.

**Required change**: MethodExtractor currently emits no hash. Add `BodyHash` (and
optionally the composed `Key`) to `catalog.json` output using *exactly* the normalization
documented in `store/meta.json`, so tool-side and store-side hashes agree. ~15 lines in
`tools/MethodExtractor/Program.cs`.

### 1.3 Verdicts belong in the store — reconciliation semantics

Yes, the store includes judged verdicts, because the single most expensive failure mode of
a re-run is **re-litigating the 49 KEEP_SEPARATE rulings** (~half the judging budget went
into discovering that labeler evidence dissolves on inspection — that knowledge must not
evaporate). Mechanism:

- Each finding in `verdicts.json` carries `anchors` (store keys) + `anchor_body12`
  (hashes at ruling time).
- **Carry-forward rule**: a KEEP_SEPARATE finding whose anchors all have unchanged
  `body12` at re-run time is carried forward verbatim — no judge spawned for it.
- **Re-judge rule**: any finding with a changed/deleted anchor, plus any *new* cluster
  evidence in its unit, goes back to a judge — with the previous ruling quoted as prior
  context ("state specifically what changed in the code to overturn this").
- **Unanchored findings** (45/116 — prose didn't name resolvable methods): always given to
  the unit's judge as prior context, never silently re-litigated from scratch. The judge
  confirms or overturns them explicitly and (new requirement) names anchor methods so the
  next run can auto-carry them.
- CONSOLIDATE findings reconcile against reality instead: if the duplicate is gone
  (consolidation batch landed), the finding is marked `resolved`; if still present,
  re-surfaced as-is (no re-judging needed — the code evidence is in the dossier).

---

## 2. Use 1 — `/dup-review`: the re-runnable full review

A new curated skill at `.claude/skills/dup-review/` (needs a `!.claude/skills/dup-review/`
un-ignore line in `.gitignore`). **User-invoked only** — the SKILL.md description must say
so explicitly ("Run ONLY when the user explicitly asks to re-run the duplication /
consolidation review; never trigger from routine coding work") so trigger-matching never
fires it spontaneously. It is a multi-hour, multi-million-token operation.

### 2.1 Files in the skill

- `SKILL.md` — the pipeline procedure (below), model tiers, budget expectations, output
  contract.
- `labeler-prompt.md` — the batch-labeling agent prompt: points at
  `store/taxonomy.md` verbatim + the batch-file protocol (input batch path, output
  `*.labels.json` schema, "every id exactly once" rule). *Must be reconstructed now* —
  the original lived only in session context; the taxonomy (its core) is persisted, the
  ~20 lines of harness instructions around it are cheap to rewrite.
- `judge-prompt.md` — the unit-judge prompt: read the unit dossier/evidence, read the
  actual code at HEAD, prior-verdict reconciliation rules (§1.3), verdict format
  (`CONSOLIDATE/INVESTIGATE/KEEP_SEPARATE` + confidence + effort/risk + anchors),
  dossier format.

### 2.2 Pipeline (as SKILL.md will specify it)

| Step | What | Who/model | Notes |
|---|---|---|---|
| 0 | Preconditions | main session | Clean-ish tree, `dotnet build` passes; warn if uncommitted churn (labels would describe a moving target). Record HEAD commit for meta.json. |
| 1 | Extract | `dotnet build tools/MethodExtractor` + `tools/run_method_extractor.py cache/method_extract` | ~1 min. |
| 2 | Diff vs store | `tools/label_store.py diff` | Buckets: unchanged / changed / moved (relink) / new / deleted. Prints drift %. |
| 3 | Relink + prune | `label_store.py relink` | Transfers labels across renames/moves by body12; prunes deleted; refreshes line numbers. Zero LLM cost. |
| 4 | Label the drift | Sonnet subagents, one per batch | Only new+changed methods, batched ~150k chars (extractor already batches; add a filter to stale ids). 27 batches ≈ full codebase; typical re-run ≈ 3–8 batches. |
| 5 | Import labels | `label_store.py import` | Validates every id answered, updates store + meta. |
| 6 | Cluster | `tools/cluster_labels.py` (updated to read the store) | Seconds. |
| 7 | Concept audit | main session (Fable) | Cross the cluster summary + dup_hints against `docs/locate-behavior/overview.md`'s subsystem inventory → 15–25 investigation units. Reuse the previous unit list as the starting frame; add units only for new/changed concept areas. |
| 8 | Reconcile | `label_store.py reconcile` | Applies §1.3: carried / resolved / needs-rejudge per finding; emits the judge worklist. |
| 9 | Judge | Fable subagents, one per unit needing judgment | Each gets: unit evidence, prior dossier, prior verdicts for the unit, judge-prompt.md. Must read code at HEAD; must emit anchors for every finding. |
| 10 | Publish | main session | Rewrite `verdicts.md` + changed `dossiers/*`, update `store/verdicts.json` + `meta.json`, append actionable items to `memory/consolidation_opportunities.md`, refresh the README date/counts. Offer a single commit (per git policy — orchestrated by the user session, this skill itself makes no pushes). |

### 2.3 Budget expectations (state these in SKILL.md so the user can abort)

- **From scratch** (no usable store, or taxonomy changed): ≈ 2.6M Sonnet (labeling) +
  ~0.3M (audit) + ~1.8M Fable (judging) ≈ **4.5–5M tokens**, several hours wall time.
- **Typical re-run** (store valid, 10–30% method drift since last review): labeling
  0.3–0.8M + judging only changed/new units 0.5–1M + audit/publish overhead ≈
  **1.5–2.5M tokens**, roughly 1–2 hours.
- Hard rule: if `diff` reports <5% drift, tell the user a full review is probably not
  worth it yet and ask before proceeding.

### 2.4 Taxonomy evolution

The taxonomy may only change as part of a `/dup-review` run (never ad hoc), because labels
are only comparable within one taxonomy version. If a run adds/renames verbs:
additive-only changes (new verb) keep old labels valid; renames/splits require mapping old
verbs forward in `label_store.py import` (record the mapping in meta.json and bump
`schema`). Full re-label only if the change is genuinely structural — that's the ~2.6M
worst case, which is why the bar for taxonomy edits is high.

---

## 3. Use 2 — authoring-time similar-code lookup

### 3.1 Options evaluated

**(a) Standalone `/find-similar` skill.** Works, but competes with `locate-behavior` for
the same trigger ("I'm about to add X — where/what exists?"). CLAUDE.md already routes
every "where should I add Z" through locate-behavior; a second skill on an adjacent
trigger dilutes routing discipline, and sessions would have to remember two entry points.
Rejected as *primary*; the underlying CLI serves ad-hoc use anyway.

**(b) CLAUDE.md directive only.** Cheapest, but directives without machinery decay —
"label it and query the store" is exactly the kind of instruction that gets skipped under
task pressure. Kept as a *pointer* (one line), not as the mechanism.

**(c) PostToolUse hook on Edit/Write.** Rejected. Fires on every edit including trivial
ones; a hook can only nag, not label (labeling is semantic judgment); the repo's hook
philosophy (`docs/avoid-prompting-user.md`) is about *removing* friction, and this adds a
reminder to the hottest path in every session. A narrowly-scoped variant — reminder only
when a Write creates a *new* `.cs` file — is defensible and listed as an optional later
experiment, off by default.

**(d) Integrate into `locate-behavior`.** Was the design's recommended primary — **but the
user DECLINED it.** find-similar is not wired into the finder; use the manual
`tools/label_store.py query` CLI (option below) instead. The original rationale is kept
here for the record, not as an active plan:
- "Where should this code go" and "what similar code already exists" are the same lookup
  at different zoom levels, and locate-behavior is *already mandatory* at the start of any
  change per CLAUDE.md — zero new routing burden.
- The finder runs in an isolated context, so the query output (top-N candidate dumps,
  verification reads) stays out of the main session — exactly the pattern the skill was
  built for.
- The finder already self-verifies with Read/Grep; store hits get the same treatment
  (the store is a recall aid, not ground truth — stale summaries get caught on
  verification).

### 3.2 Recommended design

**Primary (as-built) — manual `tools/label_store.py query`.** The user declined wiring this
into the locate-behavior finder, so the CLI *is* the authoring-time path: run it directly
when you want to know whether similar code already exists. The finder can be *asked* to run
the query, but nothing routes to it automatically.

**Declined design (kept for the record) — extend the locate-behavior finder** (would have
edited `docs/locate-behavior/README.md` + a paragraph in the skill's SKILL.md; the finder
agent shim's tool list already includes Bash). Under it, when the ask involved *adding* code
(vs. just finding existing behavior), the finder would additionally:
1. Derives facets for the intended code from `store/taxonomy.md` — pick verb, target,
   2–3 mechanism/name keywords. (~50 lines of taxonomy to read; the finder does this
   itself, no extra agent.)
2. Runs `tools/label_store.py query --verb V --target T --like-name "foo" --top 12`.
3. Verifies the plausible hits with Read (it already reads code to answer).
4. Adds a **"Similar existing code"** section to its answer: file:line, one-line summary,
   and a judgement ("reuse this", "extend this", "genuinely new"). Prior
   KEEP_SEPARATE verdicts touching those methods are surfaced too (`query` joins
   `verdicts.json` anchors) so the finder doesn't recommend re-merging things the review
   explicitly ruled apart.

**Fallback / post-authoring — direct CLI use**, prompted by one new CLAUDE.md line (see
checklist): after writing a significant new method (a system, a query, a pipeline step —
not a 3-line getter), the session labels it inline (it just wrote it; assigning
verb/target/mechanism costs ~a sentence of thought) and runs:

```
python tools/label_store.py add --file Necroking/X.cs --name Foo --verb query-nearest --target unit --mechanism "linear scan distance" --summary "..."
python tools/label_store.py query --like Necroking/X.cs::Type::Foo --top 10
```

If a near-duplicate surfaces, normal CLAUDE.md consolidation rules apply (reuse/extend, or
note it in `memory/consolidation_opportunities.md`). This is also how new code enters the
store between reviews instead of piling up as unlabeled drift.

### 3.3 Query mechanics (in `label_store.py`, pure python, no LLM)

Scoring per store entry (weights tunable, all cheap):
- verb: exact match required by default (`--any-verb` to relax), it's the strongest signal;
- target: exact = 1.0, synonym-table hit = 0.7 (small hand-table: unit/corpse, env-object/foragable, window/panel/widget, …);
- mechanism: token Jaccard overlap;
- name: difflib ratio over identifier tokens (CamelCase-split);
- summary: keyword overlap with `--text` terms if given.

Query modes: by explicit facets (`--verb --target --like-name`), by exemplar
(`--like <key or file:name>` — use an existing/new method's own labels as the probe), or
free-text (`--text "find nearest berry bush"` matched against summaries). Output: top-N as
`file:line  Type.Name (NNL) [verb|target] summary  {verdict-flag if anchored}`.
Deliberately **no embeddings** — 3.5k entries, facet+string scoring is instant, local,
dependency-free, and the finder verifies hits in code anyway.

---

## 4. Freshness strategy

The store tolerates drift by design — facets describe *intent*, which survives most edits;
every consumer verifies hits in real code before acting. Freshness machinery, cheapest
first:

1. **Continuous, free**: new significant methods get labeled inline at authoring time
   (§3.2 fallback flow). Sessions that materially *rewrite* a method may update its label
   the same way (re-label-on-touch — soft convention, not enforced).
2. **On demand, seconds**: `label_store.py diff` (runs the extractor to `cache/`,
   compares keys+body12) reports unchanged/changed/moved/new/deleted and a drift %.
   `relink` + line-number refresh fix the mechanical part with zero LLM cost. Worth
   running before trusting the store after a big refactor lands.
3. **Label refresh, cheap-ish**: when drift exceeds ~20%, a labels-only refresh (steps
   1–5 of the §2.2 pipeline, no audit/judging) re-labels just the stale/new methods —
   Sonnet batches, ≈ 0.5M tokens at 20% drift. Can be requested standalone ("refresh the
   dup-review labels") without a full review.
4. **Full review**: `/dup-review` (§2), which subsumes all of the above. The README
   already says "re-run after major refactors, not per-change" — that stands.

Staleness is *visible*, never silent: `meta.json` records the labeled-at commit; `diff`
quantifies rot; `query` output can flag stale entries (`body12` mismatch vs latest cached
catalog) so the finder knows to trust the summary less.

### `tools/label_store.py` — CLI spec (describe-only, ~300 lines, stdlib)

| Command | Does | Cost |
|---|---|---|
| `query` | §3.3 scoring against `labels.json` (+ verdict-anchor join) | instant |
| `diff` | run/refresh `cache/method_extract`, bucket store vs catalog | ~1 min (extractor) |
| `relink` | transfer labels across rename/move by unique body12; refresh lines; prune deleted | instant |
| `add` | insert/update one entry (key composed from catalog if present; facets from args) | instant |
| `import` | merge `*.labels.json` batch outputs into the store; validate coverage; update meta | instant |
| `reconcile` | apply §1.3 to `verdicts.json` vs current body12s; emit carried/resolved/needs-rejudge worklist | instant |
| `status` | one-paragraph health: entry count, drift %, labeled-at commit, unanchored-verdict count | ~1 min |

Supporting mods: MethodExtractor emits `BodyHash` (+optional `--files` filter for fast
partial extraction later); `cluster_labels.py` gains a mode reading the store +
`cache/method_extract` instead of a scratchpad layout.

---

## 5. Cost summary

| Operation | Agent tokens | Wall time | When |
|---|---|---|---|
| find-similar query (finder or CLI) | ~0 (script) + 1–2k to interpret | seconds | every "add new code" locate |
| Label one new/changed method inline | 1–3k (in-session, no agents) | seconds | after writing significant new code |
| `diff` / `relink` / `status` | 0 | ~1 min | after big refactors land |
| Labels-only refresh @ ~20% drift (~700 methods) | ~0.5M Sonnet | 30–60 min | drift > 20% |
| `/dup-review`, typical incremental | 1.5–2.5M (Sonnet labeling + Fable judging) | 1–2 h | user-invoked, post-major-refactor |
| `/dup-review`, from scratch / taxonomy break | 4.5–5M | several hours | rare |

The incremental path exists precisely so the 4.5M-token from-scratch cost is paid once;
everything above amortizes it.

---

## 6. Implementation checklist (for approval)

Already done this session (needs only a commit by the orchestrator):
- [x] `docs/consolidation-review/store/{taxonomy.md, labels.json, verdicts.json, meta.json}` persisted from the scratchpad (3,551 entries; 116 anchored findings).
- [x] This report.

Built this session (ordered; 1–3 unlocked everything else):
1. [x] `tools/label_store.py` — CLI per §4 spec (stdlib-only). All 7 verbs implemented and
   tested against the real store: `query` (facet/exemplar/free-text scoring + verdict-anchor
   join), `diff`, `status`, `relink`, `add`, `import`, `reconcile`. Run via
   `C:/Users/Raymo/Tools/python-3.11-embed/python.exe`.
2. [x] `tools/MethodExtractor/Program.cs` — emits `BodyHash` (comment+whitespace-stripped
   sha1[:12]) and the composed `Key` (`file::type::name::sha1(sig_no_ws)[:8]`), normalization
   per `store/meta.json`. **Validated end-to-end:** 2,657 unchanged methods fingerprint-match
   the persisted store; the ~16% that drifted are the ones the consolidation batches touched.
3. [x] `tools/cluster_labels.py` — added `--store` input mode (reads the persisted
   `labels.json`, optional line refresh from `--extract-dir`); original scratch-dir mode kept.
4. [x] `.claude/skills/dup-review/` — `SKILL.md` (pipeline §2.2, budgets §2.3,
   user-invoked-only), `labeler-prompt.md`, `judge-prompt.md`. Plus `!.claude/skills/dup-review/`
   in `.gitignore`.
5. [ ] **DECLINED by the user** — `docs/locate-behavior/README.md` "Similar existing code"
   step. Not built and not to be built; the manual `label_store.py query` CLI is the
   authoring-time path instead.
6. [ ] `CLAUDE.md` pointer lines — **not done this build** (CLAUDE.md was out of the build
   agent's territory). Still worth adding by a session that owns root files: one line under
   the consolidation section pointing at this doc + the label store, and one for the
   post-authoring `label_store.py query`/`add` convention (§3.2). Low priority.
7. [ ] Optional, later, off by default: Write-hook reminder when a *new* `.cs` file is created (§3.1c).
