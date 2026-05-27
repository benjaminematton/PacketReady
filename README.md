# PacketReady

Pre-CAQH credentialing readiness score. PDFs in, 0–100 score out,
every issue cited back to its source PDF region.

![demo](docs/assets/demo.gif)

[Higher-fidelity recording](https://github.com/benjaminematton/PacketReady/releases/tag/demo-v1) · re-record locally via [tools/demo-tour/](tools/demo-tour/).

Companion docs:

- **[docs/design.md](docs/design.md)** — full design, decision tree,
  alternatives rejected, row-by-row competitor verification.
- **[docs/build-plan.md](docs/build-plan.md)** — phase-by-phase build
  log (phases 0–5 closed; phase 6 demo polish + this README).

## How it works

Two halves: an intake agent collects the documents; a validator suite
turns them into a score.

### Architecture, one glance

```
Provider uploads PDFs
          │
          ▼
┌────────────────────────────────────────────────────────┐
│ Classify (Haiku) ─► Extract (Sonnet)       per document│
│  doc_type + conf     fields + bboxes                   │
└────────────────────────────────────────────────────────┘
          │
          ▼
┌────────────────────────────────────────────────────────┐
│ Intake agent (FSM, 5 tools, per-turn budget)           │
│   decides: follow up · score · escalate                │
└────────────────────────────────────────────────────────┘
          │ when complete
          ▼
┌────────────────────────────────────────────────────────┐
│ 8 validators in parallel                               │
│   6 pure-code · 2 LLM-augmented · 3 payer-aware        │
└────────────────────────────────────────────────────────┘
          │
          ▼
┌────────────────────────────────────────────────────────┐
│ Score synthesis: −25 Critical / −10 Major / −3 Minor,  │
│   floor at 0.  Tier: ≥85 / 60–84 / <60.                │
└────────────────────────────────────────────────────────┘
          │
          ▼
Dashboard: click any issue ─► source PDF region + audit timeline
```

### The intake half

Admin adds a provider — name and email, that's it. Provider gets a
magic-link email, clicks through to a single-page portal, drops in
whatever credentialing PDFs they have on hand (license, DEA,
malpractice, board cert). Each document is classified by Haiku, then
extracted by Sonnet into structured fields with per-field bounding
boxes back into the source PDF.

Then the **intake agent** runs one turn. It's a Claude loop with a
deliberately small five-tool surface (`read_document`,
`extract_fields`, `lookup_primary_source`, `compose_followup`,
`compute_readiness`), bounded per turn by 15 steps / 80k tokens / 90s.
It decides one of three things:

- **Score it** — invoke the terminal `compute_readiness` tool; the
  state machine transitions to the validator pipeline.
- **Follow up** — invoke `compose_followup`, which aggregates all
  current gaps into one targeted email. Not twelve templated
  reminders; one consolidated message with everything missing.
- **Escalate** — if the per-provider turn budget exhausts, transition
  to `escalated` with the partial state attached and notify a human.

Every state transition is deterministic code, not an LLM decision.
The agent lives *inside* the FSM, not above it — workflow pattern,
not autonomous agent, per Anthropic's *Building Effective Agents*.
Every agent turn writes to an append-only audit log (Postgres,
enforced by a `BEFORE UPDATE` trigger).

### The score half

When the agent declares the profile ready, eight validators fan out
in parallel:

| Validator | Type | Checks |
|---|---|---|
| `license_status` | pure code | active, correct state, not expired |
| `dea_status` | pure code | active, not expired |
| `malpractice_currency` | pure code + payer YAML | in force, coverage ≥ payer min, expiry > 30d |
| `board_certification` | pure code + payer YAML | active for stated specialty, board accepted |
| `sanctions_check` | pure code | OIG / SAM clean |
| `required_documents` | pure code + payer YAML | every payer-required doc present |
| `identity_coherence` | **LLM (Sonnet)** | name / DOB / NPI / address agree across docs |
| `npi_taxonomy_match` | **LLM (Sonnet)** | NPI taxonomy code matches stated specialty |

Six pure-code validators because field-level checks shouldn't burn
LLM budget; two LLM-augmented because cross-document fuzzy reasoning
isn't regex-able. Three are payer-aware via per-payer YAML config —
adding a new payer is config, not code.

Each validator returns `pass | minor | major | critical` with a
**citation** pointing to a specific page and bounding box on the
source PDF. Score synthesis is a transparent weighted sum:

```
Start at 100.
−25 per Critical issue.
−10 per Major issue.
−3 per Minor issue.
Floor at 0.
```

Tier: **green ≥85 · yellow 60–84 · red <60**.

One safety belt baked into the rubric: a Critical issue whose
citation references any field with extraction confidence < 0.85 is
auto-downgraded to Minor (`Issue.IsLowConfidenceInput = true`,
mirrored on `Citation.LowConfidence`). We don't fire hard rejections
on values we ourselves weren't confident about.

### Why the audit chain matters

Every issue in the dashboard is clickable. The side panel opens with
two tabs: the source PDF rendered at the right page with the amber
bbox highlight, and a vertical timeline of every audit event that
produced the issue — Haiku classification, Sonnet extraction,
validator invocation, score synthesis — each with cost and wall-clock
time.

This is the NCQA 2026 audit-trail requirement satisfied by
construction, not retrofitted. It's also the moat against incumbents
whose engineering wasn't built around immutable provenance.

## What's shipped

End-to-end, not "framework with TODOs":

- Sonnet-based per-doc extractors (license / DEA / malpractice / board
  cert) with confidence-weighted outputs and bbox-level citations.
- Eight cross-document validators (4 rule-based, 2 LLM-augmented,
  2 payer-aware via per-payer YAML config).
- 0–100 readiness score with Critical / Major / Minor breakdown and
  per-issue remediation pointing back to the source PDF region.
- Operator dashboard with worst-first triage and per-provider audit
  timeline reconstructed from append-only events.
- **Intake agent** — tool-using Claude loop with a 5-tool surface
  (`read_document`, `extract_fields`, `lookup_primary_source`,
  `compose_followup`, `compute_readiness`) driven by a per-provider
  budget; FSM with explicit terminal / follow-up / escalated transitions.
- **Magic-link provider portal** — Next.js app at
  [`portal/`](portal/); HMAC-signed single-use tokens; provider sees the
  on-file documents and submits, agent takes the next turn.
- **Outbox dispatcher** — Hangfire recurring job (`OutboxDispatcherJob`)
  drains queued provider follow-ups through an `IEmailSender` port.
  Current backend is `MockSmtpSender` (file-writes to a local maildir);
  the contract is shaped for a real Postmark / SES swap.
- Full Langfuse observability across classification, extraction,
  validation, score synthesis, and every intake-agent turn.
- 50-packet synthetic eval set, run end-to-end through the
  orchestrator, with weighted Cohen's κ = 0.68 against 20 hand-labeled
  tier judgments.

## What's mocked (and where the real swap goes)

In each case the interface is production-shape and the calling code
doesn't change when you swap the implementation — the mock just stands
in for the network call.

- **Primary-source verification.** `IPrimarySourceLookup`
  ([`Application/Intake/PrimarySources/`](apps/api/Application/Intake/PrimarySources/))
  is the seam; `MockPrimarySourceLookup` returns deterministic fixture
  responses. Live CAQH ProView, NPPES, OIG, SAM, and state board calls
  go here.
- **SMTP backend.** `IEmailSender` is the port;
  [`MockSmtpSender`](apps/api/Infrastructure/Outbox/) writes RFC-822 to
  a local maildir so the outbox dispatcher's behavior is observable in
  tests + the demo. Postmark / SES / Mailgun all fit the port.
- **Blob storage.** `IBlobStore` is local-filesystem in dev
  (`LocalFileBlobStore`). S3 / GCS swap is one registration line.

## What's out of scope

- **Browser-driven payer portal submission.** Atano already does this.
- **Production authn/authz.** Magic-link is the only auth surface, no
  org or role model.
- **HIPAA-compliant deployment posture.** Synthetic data only.

## Accuracy

Two number sets. **Full-pipeline baseline** runs the 50-packet dataset
end-to-end through classification, extraction, validators, and score
synthesis. **Prompt-isolated tuning** measures individual LLM-validator
prompts against `golden.json` inputs directly, bypassing extraction.
Comparing the two locates any gap (extraction vs validator).

### Full-pipeline baseline

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
the Issue's `field` discriminator matches the planter's field. See
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
name-variant, taxonomy-mismatch). Labeler tier distribution: 8 Green,
2 Yellow, 10 Red.

| Metric                              | Value      | Floor                                          |
|---|---:|---|
| Weighted Cohen's κ (quadratic)      | **0.6786** | Landis-Koch substantial 0.61; target 0.50      |
| Raw agreement                       | 0.55       | 11 / 20 exact matches                          |
| Spearman ρ (score vs ordinal tier)  | 0.7455     | continuous footnote, not headline              |

3×3 confusion (rows = human tier, cols = system tier):

|           | Red | Yellow | Green |
|---|---:|---:|---:|
| **Red**    | 2 | 8 | 0 |
| **Yellow** | 0 | 2 | 0 |
| **Green**  | 0 | 1 | 7 |

The system trends conservative on Red: 8 of 10 human-Red packets came
back system-Yellow. Never returned Red for a human-Green case, never
Green for a human-Red. The κ holds at 0.68 because off-by-one slips
dominate over catastrophic swaps under quadratic weighting. Fix is
rubric reweighting on Critical issues.

Note: ~1/3 of clean/scanned packets read Red due to a deterministic
generator artifact (DEA expiry lands on the anchor date); kept as-is
for κ stability. Both conflict metrics are next-tuning surfaces —
`name_variant` recall reflects FP-discipline tuning on the v1 prompt;
`taxonomy_specialty_mismatch` precision reflects the v1 NUCC compare
prompt. See
[phase-4 doc](docs/impl/phase-4-scale-and-llm-validators.md).

### IdentityCoherence prompt-isolated tuning

Measures the IdentityCoherence prompt against `golden.json` fullName
values directly via `tools/TuneIdentityCoherence` — no PDFs, no
classifier, no Sonnet extractor. Source data:
[`evals/tuning-runs/iter-100__*.json`](evals/tuning-runs/), prompt SHA
`48322ce7`, 50 packets × 3 runs, worst-of.

False-positive rate on the 30 conflict-free + 3 don't-flag packets:

|                       | Rate |
|---|---|
| **FP rate**           | **0.0%** (0 fabrications across 30 clean + 3 don't-flag packets) |

**100% recall (9/9) across 4 must-flag shapes** — hyphenated suffix,
middle-name added, nickname, surname swap. SURNAME_TYPO (one-letter
typo) correctly not flagged.

Tuning converged in two instruction-level iterations from baseline
FP=16.7% / recall=75% to FP=0% / recall=100% on the tuning subset.
[Full iteration log](evals/tuning-runs/) and
[per-iteration failures TSV](evals/tuning-runs/iter-00__failures.tsv).
Held-out 10 (disjoint from the tuning subset, drawn from the 50 with
`seed=9999`) ran 3 times and matched the in-sample numbers.

### Bias caveat

The hand-labeled fixtures, the IdentityCoherence and NpiTaxonomyMatch
prompts' do-flag / don't-flag rules, the iteration decisions during
prompt tuning, and the 20-packet Red/Yellow/Green tier labeling were
all made by the same person. The published numbers measure how well
the system reproduces that one person's credentialing judgment, not
how well it tracks an independent ground truth.

Read every published metric — `name_variant` precision/recall,
`taxonomy_specialty_mismatch` precision, κ = 0.6786 — as an upper
bound on agreement with an independent expert. Two structural facts:
labeler is also the validator-rule author, and not a working
credentialing admin. The `_biasNote` field in
[`human_tiers.json`](evals/labels/human_tiers.json) carries this
in-band; `agreement.biasNote` carries it through to the
regression-gate payload.

### Competitor positioning

The defensible column intersection — pre-CAQH intake + cross-document
validation + a published readiness score + a cited audit trail +
published accuracy numbers — is shipped by **no single competitor** as
of the 2026-05 marketing-surface verification in
[docs/design.md §Appendix A](docs/design.md#appendix-a--comparison-to-competitors).
That appendix carries the row-by-row verification (each "✓" or
"partial" sourced to a competitor's homepage) and a per-row reading
notes block documenting what every "—" represents.

## Local setup

See [docs/local-setup.md](docs/local-setup.md) for prerequisites,
bring-up steps, the Phase 0 smoke-test gate, repo layout, and common
dev commands.
