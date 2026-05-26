# PacketReady

Pre-CAQH provider intake agent that ends in a cross-document packet readiness score.

See [docs/design.md](docs/design.md) for the full design and [docs/build-plan.md](docs/build-plan.md) for the build roadmap. Phase 0 details are in [docs/impl/phase-0-walking-skeleton.md](docs/impl/phase-0-walking-skeleton.md).

## Status

**Phase 4 (scale + LLM validators)** — in progress. Phases 0–3 closed; the
50-packet eval set is generated, the first LLM validator
(`IdentityCoherenceValidator`) has converged on the tuning gate.

| Phase | State |
|---|---|
| 0 — Walking skeleton (ping + audit + trace) | ✓ closed |
| 1 — Score from clean input (rule-based validators) | ✓ closed |
| 2 — Eval harness + 5 hand-crafted packets | ✓ closed |
| 3 — Per-doc-type Sonnet extractors + aggregator | ✓ closed |
| 4 — 50-packet eval + LLM validators + payer-aware validators | in progress |

## Accuracy

Numbers below come from the [iter-100 baseline run](evals/tuning-runs/) —
the converged IdentityCoherence prompt (SHA `48322ce7`), applied via
`tools/TuneIdentityCoherence` to all 50 packets × 3 runs, worst-of metrics.
The full record (per-packet emissions, tokens, cost) is committed under
[`evals/tuning-runs/iter-100__*.json`](evals/tuning-runs/).

### IdentityCoherence cross-doc identity validator (P4 task 8–9)

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

Other P4 validators (`npi_taxonomy_match`, `malpractice_currency`,
`required_documents`, the payer-aware `BoardCertificationValidator` extension)
are pending; their numbers will land alongside the conflict-precision/recall
table once
[`evals/runners/runners/conflict_metrics.py`](evals/runners/runners/) does
per-validator-kind slicing across the 50 packets.

### Bias caveat (read this before citing the numbers)

The hand-labeled fixtures, the IdentityCoherence prompt's do-flag /
don't-flag rules, and the iteration decisions during prompt tuning were
all made by the same person. The published FP/recall numbers measure how
well the system reproduces that one person's credentialing judgment, not
how well it tracks an independent ground truth.

A second labeler — and a second prompt reviewer — would push the bound on
this from upper to honest. Both are post-launch asks, not P4 gates. The
honest reading of `FP=0% / recall=100%` is "the validator does what the
prompt-author would have done on these 50 packets," not "the validator
catches every real-world disagreement a credentialing admin would flag."

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
