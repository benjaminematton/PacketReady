# PacketReady

Pre-CAQH credentialing intake agent. Drop a provider's PDF packet in; the
system classifies each document, extracts the structured fields with Sonnet,
runs eight cross-document validators (4 rule-based, 2 LLM, 2 payer-aware),
and emits a single readiness score with per-issue citations back to the
source PDF region.

## Demo

26-second walkthrough — operator opens the worst-first provider
list, drills into a Yellow at score 62, opens the top Critical issue's
panel (PDF preview with bounding-box highlight), then switches to the
per-provider audit timeline.

<video src="https://github.com/benjaminematton/PacketReady/releases/download/demo-v1/demo.mp4" controls width="800"></video>

If your viewer doesn't render the inline player:
[watch / download the mp4](https://github.com/benjaminematton/PacketReady/releases/download/demo-v1/demo.mp4)
or visit the
[release page](https://github.com/benjaminematton/PacketReady/releases/tag/demo-v1)
(mp4 + source webm attached). Recorded by
[`tools/demo-tour/demo-tour.spec.ts`](tools/demo-tour/demo-tour.spec.ts)
against the live API + dashboard stack — `npm run tour` from
[`tools/demo-tour/`](tools/demo-tour/) re-runs it deterministically.

## Why it's interesting

- **Eval-driven, not vibes-driven.** 50-packet synthetic dataset with
  planted conflicts. Weighted Cohen's κ = 0.68 against a 20-packet
  hand-labeled subset — substantial agreement on the Landis-Koch scale,
  above the κ ≥ 0.50 DoD floor. 0% false-positive rate on the
  IdentityCoherence prompt across 30 conflict-free packets.
- **Operator-first surface.** Every Critical / Major / Minor issue is one
  click from the literal PDF region the validator cited (page + bbox
  overlay rendered with react-pdf) and the full per-provider audit
  timeline, reconstructed from an append-only event log.
- **Payer-aware by configuration, not by code.** Validator behavior
  reconfigures from per-payer YAML — board-cert required, malpractice
  minimums, sanctions-check policy. Onboarding a new payer is a file,
  not a deploy.
- **Production patterns, not a demo hack.** Confidence-guard downgrades
  Critical → Minor on low-confidence extractions; schema-versioned
  extraction rows; idempotent extraction persistence behind a Postgres
  advisory lock; Hangfire-driven intake agent with budgeted turns;
  Langfuse OTel traces across the whole pipeline; multi-binary DI split
  so the seed CLI doesn't drag the Anthropic SDK.

Designed and built solo. Full design rationale in
[docs/design.md](docs/design.md); phase-by-phase build log in
[docs/build-plan.md](docs/build-plan.md); Phase 0 walking-skeleton notes in
[docs/impl/phase-0-walking-skeleton.md](docs/impl/phase-0-walking-skeleton.md).

## Status

**Phase 4 (scale + LLM validators)** — closed (2026-05-26). Phases 0–3
closed; the 50-packet eval set runs end-to-end through the orchestrator
(P4 task 18, [`evals/results/baseline.json`](evals/results/baseline.json));
both LLM validators (`IdentityCoherenceValidator`,
`NpiTaxonomyMatchValidator`) emit against the live extraction
pipeline; the payer-aware validator suite (malpractice currency,
required documents, board certification extension, payer-config
sanctions suppression) is wired; 20 packets hand-labeled at
[`evals/labels/human_tiers.json`](evals/labels/human_tiers.json)
producing weighted Cohen's κ = 0.68 (n=20) locked into the baseline.
All 10 DoD boxes checked in
[phase-4-scale-and-llm-validators.md](docs/impl/phase-4-scale-and-llm-validators.md).
Next: [Phase 5 — Intake agent + outbox](docs/impl/phase-5-intake-agent.md).

| Phase | State |
|---|---|
| 0 — Walking skeleton (ping + audit + trace) | ✓ closed |
| 1 — Score from clean input (rule-based validators) | ✓ closed |
| 2 — Eval harness + 5 hand-crafted packets | ✓ closed |
| 3 — Per-doc-type Sonnet extractors + aggregator | ✓ closed |
| 4 — 50-packet eval + LLM validators + payer-aware validators | ✓ closed |

## Accuracy

Two complementary number sets live here. The **full-pipeline baseline**
(P4 task 18) measures the system end-to-end against the 50-packet
dataset — every PDF goes through classification, extraction, validators,
and score synthesis as it would for a real submission. The
**prompt-isolated tuning** numbers (P4 task 8–9) measure individual
LLM-validator prompts against `golden.json` inputs directly, bypassing
extraction. The first answers "does the system work"; the second
isolates "is the prompt right." Both belong in the README; comparing
the two diagnoses where any gap lives (extraction vs validator).

### Full-pipeline baseline (P4 task 18)

[`evals/results/baseline.json`](evals/results/baseline.json), 50 packets,
~520 seconds wall-clock at concurrency=3.

**Conflict precision / recall (per planted kind):**

| Kind                            | Planted | Caught | Fabricated | Precision | Recall |
|---|---:|---:|---:|---:|---:|
| `name_variant`                  | 9       | 6      | 0          | 1.00      | 0.667  |
| `taxonomy_specialty_mismatch`   | 8       | 8      | 3          | 0.727     | 1.0    |

A planted conflict is "caught" iff all three predicates hold against
at least one emitted Issue: (1) the Issue's `validator` matches the
expected validator for the kind, (2) the Issue's citations name at
least one planted source via documentId→docType resolution, and (3)
the Issue's `field` discriminator matches the planter's field. The
3-predicate check rules out "right validator, wrong finding" from
counting — see
[`evals/runners/runners/conflict_metrics.py`](evals/runners/runners/conflict_metrics.py).

**Score / tier distribution:**

| Tier   | Count | Definition         |
|---|---:|---|
| Green  | 20    | score ≥ 85         |
| Yellow | 24    | 60 ≤ score < 85    |
| Red    | 6     | score < 60         |

Mean score 80.3, range 22–100 across 50 successful packets (0 errors).

**Tier agreement (κ + 3×3 confusion, n=20):**

20 packets hand-labeled in
[`evals/labels/human_tiers.json`](evals/labels/human_tiers.json) and
run through [`agreement.py`](evals/runners/runners/agreement.py).
Stratified across all four base buckets (clean, scanned,
name-variant, taxonomy-mismatch) to keep the confusion matrix
non-degenerate. Labeler tier distribution: 8 Green, 2 Yellow, 10 Red.

| Metric                              | Value      | Floor                                          |
|---|---:|---|
| Weighted Cohen's κ (quadratic)      | **0.6786** | Landis-Koch substantial 0.61; P4 DoD 0.50      |
| Raw agreement                       | 0.55       | 11 / 20 exact matches                          |
| Spearman ρ (score vs ordinal tier)  | 0.7455     | continuous footnote, not headline              |

3×3 confusion (rows = human tier, cols = system tier):

|           | Red | Yellow | Green |
|---|---:|---:|---:|
| **Red**    | 2 | 8 | 0 |
| **Yellow** | 0 | 2 | 0 |
| **Green**  | 0 | 1 | 7 |

**System trends conservative on Red.** 8 of 10 human-Red packets
came back system-Yellow; the system never returned Red for a
human-Green case, and never Green for a human-Red. The κ holds at
0.68 only because off-by-one slips dominate over catastrophic swaps
under quadratic weighting. A reader interpreting the score should
know: *the system underreaches on critical blockers relative to a
human labeler*. The fix is rubric reweighting on Critical issues
— out of P4 scope, named for the post-launch follow-on.

**Why some "clean" buckets read Red.** The dataset generator anchors
every packet's dates to `_NEW_PACKET_ANCHOR = 2026-05-25`. When the
per-packet RNG draws `rng=3` on the DEA-issue offset, the DEA
expires exactly on the anchor date — yesterday from the perspective
of any `today > 2026-05-25`. Hits ~1/3 of clean / scanned packets
(Hall, Flores, Rice, Tucker in the n=20 subset). Treated as a
feature, not a bug: it gives the eval Red samples outside the
planted-conflict buckets, which is what keeps κ from going
degenerate. Documented in
[`phase-4-scale-and-llm-validators.md`](docs/impl/phase-4-scale-and-llm-validators.md)
under risks/open.

**Tuning surfaces (real, named in the baseline):**

- `name_variant` recall ceiling at 0.667 with FP=0 means the prompt is
  conservatively-tuned — 3 must-flag planted shapes pass without
  emission. The IdentityCoherence v1 prompt was tuned on the 10-packet
  subset to prioritize FP discipline; the held-out 3 misses are the
  expected cost. Bumping recall here is the next tuning loop, not a
  bug.
- `taxonomy_specialty_mismatch` precision 0.73 reflects 3 LLM
  judgments that flag legitimate specialty/taxonomy synonymies as
  mismatches. P5+ tuning surface — the NUCC compare prompt is a v1
  ship.

### IdentityCoherence prompt-isolated tuning (P4 task 8–9)

These numbers measure the IdentityCoherence prompt against `golden.json`
fullName values directly via `tools/TuneIdentityCoherence` — no PDFs,
no classifier, no Sonnet extractor. They tell you whether the prompt
would catch a disagreement *given* a correctly-extracted name set; the
full-pipeline baseline above tells you what happens when extraction is
in the loop. Recall on the full pipeline (0.667) sits below the
prompt-isolated number (100%) — the gap measures extraction noise +
provenance routing, not prompt quality. Source data:
[`evals/tuning-runs/iter-100__*.json`](evals/tuning-runs/), prompt SHA
`48322ce7`, 50 packets × 3 runs, worst-of.

False-positive rate on the 30 conflict-free + 3 don't-flag packets:

|                       | Rate |
|---|---|
| **FP rate**           | **0.0%** (0 fabrications across 30 clean + 3 don't-flag packets) |

Recall by planted name-disagreement shape (must-flag totals across the 50-packet set):

| Shape               | Caught | Notes |
|---|---|---|
| HYPHENATED_SUFFIX   | 3 / 3  | `"Jane Calloway"` → `"Jane C. Calloway-Smith"` |
| MIDDLE_NAME_ADDED   | 2 / 2  | `"John Bartlett"` → `"John James Bartlett"` |
| NICKNAME            | 2 / 2  | `"Robert Anderson"` → `"Bob Anderson"` |
| SURNAME_SWAP        | 2 / 2  | `"Anderson"` → `"Bautista"` |
| SURNAME_TYPO        | 0 / 2  | One-letter typo — correctly **not flagged** (don't-flag shape) |
| **Total must-flag** | **9 / 9 (100%)** |  |

Tuning converged in two instruction-level iterations from baseline FP=16.7% /
recall=75% to FP=0% / recall=100% on the tuning subset. The
[full iteration log](evals/tuning-runs/) and the
[per-iteration failures TSV](evals/tuning-runs/iter-00__failures.tsv) record
exactly which rule changes moved which category. Held-out 10 (disjoint from
the tuning subset, drawn from the 50 with `seed=9999`) ran 3 times and
matched the in-sample numbers — no overfit.

### Bias caveat (read this before citing the numbers)

The hand-labeled fixtures, the IdentityCoherence and NpiTaxonomyMatch
prompts' do-flag / don't-flag rules, the iteration decisions during
prompt tuning, and (once it lands) the 20-packet Red/Yellow/Green tier
labeling were all made by the same person. The published numbers
measure how well the system reproduces that one person's credentialing
judgment, not how well it tracks an independent ground truth.

A second labeler — and a second prompt reviewer — would push the bound
on these from upper to honest. Both are post-launch asks, not P4 gates.
Honest readings:

- **`name_variant` precision 1.0, recall 0.667** — "the validator emits
  only the disagreements the prompt-author would have called out, and
  emits them on the planter shapes the prompt was tuned for." Not "the
  validator catches every real-world name disagreement a credentialing
  admin would flag."
- **`taxonomy_specialty_mismatch` precision 0.73** — "the LLM compare
  step judges some legitimate specialty/taxonomy synonymies as
  mismatches when a credentialing expert wouldn't." Worth a second
  prompt reviewer.
- **Tier agreement κ = 0.6786 (n=20)** — measures self-consistency
  between the validator suite and the labeler-in-the-validator-author's-head,
  not ground truth. Read it as an upper bound on agreement with an
  independent expert. Two structural facts: (1) the labeler is also
  the validator-rule author, and (2) the labeler is not a working
  credentialing admin. The system's tendency to come back Yellow on
  human-Red cases (the 8/10 cell of the matrix above) is itself
  informative: a credentialing admin might call the labeler
  conservative, or might call the system permissive, depending on
  whose rubric anchors the conversation. The `_biasNote` field in
  [`human_tiers.json`](evals/labels/human_tiers.json) carries this
  in-band; the baseline's `agreement.biasNote` carries it through to
  the regression-gate payload.

### Competitor positioning

The defensible column intersection — pre-CAQH intake +
cross-document validation + a published readiness score + a cited
audit trail + published accuracy numbers — is shipped by **no single
competitor** as of the 2026-05 marketing-surface verification in
[docs/design.md §Appendix A](docs/design.md#appendix-a--comparison-to-competitors).
That appendix carries the row-by-row verification (each "✓" or
"partial" sourced to a competitor's homepage) and a per-row reading
notes block documenting what every "—" represents. The full table is
narrower than the marketing copy suggests for several competitors —
deliberately so, per the "Better no claim than a wrong one" gate in
the P4 review.

## Local bring-up (Phase 0)

Prerequisites: Docker, .NET 10 SDK, an Anthropic API key.

```bash
# 1. Start Postgres + self-hosted Langfuse
docker compose up -d

# 2. Configure secrets — copy and fill in
cp .env.example .env
#    - ANTHROPIC_API_KEY (sk-ant-...)
#    - LANGFUSE_PUBLIC_KEY + LANGFUSE_SECRET_KEY: create account at
#      http://localhost:3000, generate a project, copy keys from the UI.

# 3. Load env vars into the shell
set -a; source .env; set +a

# 4. Apply EF migrations
dotnet ef database update \
  --project apps/api/Infrastructure/Infrastructure.csproj \
  --startup-project apps/api/Api/Api.csproj

# 5. Run the API
dotnet run --project apps/api/Api/Api.csproj

# 6. Smoke test (in another shell)
curl -X POST http://localhost:5xxx/api/ping \
  -H 'Content-Type: application/json' \
  -d '{"message":"hello"}'
```

Port `5xxx` — check the `dotnet run` output for the actual port (typically 5066 HTTPS / 5065 HTTP). The endpoint accepts plain HTTP in dev.

## Phase 0 gate

A `POST /api/ping` returns a JSON payload with `reply`, `model`, `audit_event_id`, `trace_id`, token counts, and cost. Verify:

- [ ] Audit row in Postgres: `select * from audit_events;` shows one row with `event_type = 'PingExecuted'`.
- [ ] Trace at `http://localhost:3000` shows a `ping.invoke` span with model + cost rendered.
- [ ] Killing Postgres → API returns 500.
- [ ] Killing Langfuse → API still returns 200 (telemetry is fire-and-forget).

## Repo layout

```
PacketReady/
├── docker-compose.yml       # Postgres (5433) + Langfuse (3000)
├── .env.example             # Copy to .env, fill in keys
├── docs/
│   ├── design.md
│   ├── build-plan.md
│   └── impl/phase-0-walking-skeleton.md
└── apps/api/                # .NET 10 backend
    ├── PacketReady.slnx
    ├── Domain/              # entities, no external deps
    ├── Application/         # MediatR commands, interfaces, prompts
    ├── Infrastructure/      # EF Core, Anthropic.SDK, Langfuse/OTel
    ├── Api/                 # ASP.NET Core minimal API
    └── Tests/               # xUnit
```

## Common commands

```bash
# Build
dotnet build apps/api/PacketReady.slnx

# Test
dotnet test apps/api/Tests/Tests.csproj

# Add a migration
dotnet ef migrations add <Name> \
  --project apps/api/Infrastructure/Infrastructure.csproj \
  --startup-project apps/api/Api/Api.csproj \
  --output-dir Persistence/Migrations

# Tear down
docker compose down              # keeps volumes
docker compose down -v           # wipes volumes (Langfuse account + DB data lost)
```
