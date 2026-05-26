# PacketReady

Pre-CAQH provider intake agent that ends in a cross-document packet readiness score.

See [docs/design.md](docs/design.md) for the full design and [docs/build-plan.md](docs/build-plan.md) for the build roadmap. Phase 0 details are in [docs/impl/phase-0-walking-skeleton.md](docs/impl/phase-0-walking-skeleton.md).

## Status

**Phase 4 (scale + LLM validators)** — closing. Phases 0–3 closed; the
50-packet eval set runs end-to-end through the orchestrator (P4 task
18, [`evals/results/baseline.json`](evals/results/baseline.json));
both LLM validators (`IdentityCoherenceValidator`,
`NpiTaxonomyMatchValidator`) emit against the live extraction
pipeline; the payer-aware validator suite (malpractice currency,
required documents, board certification extension, payer-config
sanctions suppression) is wired. Task 16 (hand-labeling 20 packets
for tier agreement) and task 22 (DoD walk) are the remaining
human-only steps.

| Phase | State |
|---|---|
| 0 — Walking skeleton (ping + audit + trace) | ✓ closed |
| 1 — Score from clean input (rule-based validators) | ✓ closed |
| 2 — Eval harness + 5 hand-crafted packets | ✓ closed |
| 3 — Per-doc-type Sonnet extractors + aggregator | ✓ closed |
| 4 — 50-packet eval + LLM validators + payer-aware validators | closing (16/22 pending) |

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

**Tier agreement (κ + 3×3 confusion):** *pending hand-labeling
(P4 task 16) of 20 packets into
[`evals/labels/human_tiers.json`](evals/labels/) before the
orchestrator can compute it. The
[`agreement.py`](evals/runners/runners/agreement.py) module is shipped
and tested; first numbers land when the labels do.*

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
- **Tier agreement κ** — when the hand-labeled tiers land, the κ value
  measures self-consistency between the validator suite and the
  labeler-in-the-validator-author's-head, not ground truth. The
  `_biasNote` field in [`human_tiers.json`](evals/labels/) carries this
  in-band; the README publishes it here in plain language.

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
