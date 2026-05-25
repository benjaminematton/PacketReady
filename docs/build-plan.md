# PacketReady — Build Plan

> Strategic sequencing for the build. Living doc. Solo journal style — terse, decisions over prose, gates over estimates. Per-subsystem deep-dives written JIT at the start of each phase.

| | |
|---|---|
| **Owner** | Ben |
| **Started** | 2026-05-21 |
| **Last updated** | 2026-05-22 |
| **Data** | synthetic only · mocked PSV · no PHI |
| **Companion doc** | [design.md](./design.md) |
| **Style** | [style.md](./style.md) — voice, spine, callout vocabulary |
| **Conventions** | [conventions.md](./conventions.md) — code-side rules the language doesn't pin |

---

## North star

**Demoable artifact:** the 5-minute demo in [design.md §13](./design.md) runs end-to-end on a laptop with no manual intervention. README publishes per-field extraction accuracy + cross-doc conflict precision/recall.

**Done enough to ship the interview package** is the bar — not "production-ready credentialing software." Synthetic data only, mocked PSV, no HIPAA, magic-link auth only. Resist every "what about real X" pull.

**The differentiated thing is the score.** Everything else (intake, dashboard, eval harness) exists to make the score legible and defensible. Build order follows that ranking.

---

## Sequencing principle

Three rules drive the phase order below.

**1. Hardest claim first.** The 0–100 score over cross-doc reasoning on noisy real extractions is the central claim. Build the score path against curated input early (Phase 1), so the rest of the build is in service of a known target rather than discovery.

**2. Eval harness before optimization.** Don't tune extractor prompts without a regression suite. The harness lands in Phase 2 (skeleton + 5 packets) so every subsequent extractor change is measured. Scales to 50 in Phase 4.

**3. Each phase ends on a demoable gate.** Solo build motivation is fragile. A phase that doesn't produce something to look at is a phase that drifts. If a phase can't end on a gate, split it.

Rejected alternative orders:
- *Bottom-up (document store → extractors → validators → score):* orderly but no demo until phase 4+. Bad for solo morale.
- *Intake-first (state machine → tools → outbox → then score):* reproduces the §10.2 mistake — looks like a less-featured Verifiable until the score lands at the very end.

---

## Phase map

```
P0  Walking skeleton    →  audit row + Langfuse trace end-to-end
P1  Score from clean input  →  hand-curated profile → score + drill-in
P2  Eval harness + 5 packets  →  accuracy table printed
P3  Extractors + classifier  →  PDFs in → score out (no agent)
P4  Scale to 50 + LLM validators  →  README numbers published
P5  Intake agent + outbox  →  magic-link round trip → score
P6  Demo polish  →  recorded 5-min walkthrough
```

Each phase 1–2 weeks solo at full focus. Estimates are intentionally vague — gates are the truth.

---

## Phase 0 — Walking skeleton

**Goal:** prove the boring infrastructure works before any product code lands on top of it.

**Builds:**
- New repo at `/Users/benjaminmatton/Developer/PacketReady` (already created).
- .NET 10 solution: `apps/api` (web API + workers in one process for now).
- Postgres via docker-compose. Single migration: `audit_events` table only.
- Port `IInquiryLogWriter` + `InquiryLog` → `IAuditWriter` + `AuditEvent`. Rename namespace `PacketReady`.
- Port `PromptLoader` + `IPromptLoader` + `PromptResourceValidator`. No prompts yet.
- Langfuse local instance (docker). Verify a trace renders.
- Claude API client wired (Anthropic SDK, env-var auth).
- One endpoint: `POST /api/ping` → calls Claude with "hello" → writes one audit row → emits one Langfuse span → returns the response.

**Gate:** hit `/api/ping`, see the audit row in Postgres, see the span in Langfuse, see the cost in the response.

**Risks:**
- Langfuse self-host vs hosted choice. Default to self-host for privacy story; switch later if friction.
- .NET 10 GA status — confirm SDKs are stable.

**Decision log seeds:**
- Solution layout: single project vs split (Domain/Application/Infrastructure)? Default to VaBene-style split.
- Hangfire vs Quartz vs raw `IHostedService`? Default to Hangfire (matches VaBene; UI is useful for demo).

---

## Phase 1 — Score from clean input

**Goal:** prove the score logic + dashboard work before any extraction exists. Curated `ProviderProfile` JSON → readiness score with cited issues → rendered in UI.

**Builds:**
- Domain: `Provider`, `ProviderProfile`, `Issue`, `ReadinessScore`, `Citation`, `ValidatorResult`.
- 4 pure-code validators: `license_status`, `dea_status`, `board_certification`, `sanctions_check`.
- Score synthesis (`-25 / -10 / -3` rubric from §7.7).
- Endpoint: `POST /api/providers/{id}/score` with a `ProviderProfile` JSON body → `ReadinessScore`.
- Dashboard skeleton (Next.js 15, App Router): provider list + score badge + side-panel drill-in. Citations stubbed (no source PDF yet — show the validator's extracted_value + remediation only).
- 3 hand-curated profiles checked in to `evals/fixtures/`: one green, one yellow, one red.

**Gate:** load the dashboard, click each fixture provider, see different scores + tier colors + per-issue remediation. Drill-in opens to a panel with validator name, message, citation stub, remediation.

**Risks:**
- The rubric tuning question — `−25 / −10 / −3` is a guess. Don't tune until Phase 4 has correlation data.
- Dashboard scope creep. Strictly: list, score badge, drill-in panel. No filters, no search, no auth UI.

**Decision log seeds:**
- Citation shape: settle on `{ document_id, page, bbox, extracted_value, source_validator }`. Bbox is `[x, y, w, h]` in PDF user-space units.
- Tier thresholds (`≥85 / 60–84 / <60`) are inherited from design doc — leave alone until Phase 4 data says otherwise.

---

## Phase 2 — Eval harness + 5 packets

**Goal:** the regression suite exists before extractor work starts.

**Builds:**
- 5 hand-curated synthetic provider packets in `evals/dataset/`:
  - 2 clean + valid (sanity)
  - 2 clean + planted conflicts (one name-variant, one expiry-mismatch)
  - 1 scanned (rasterized + skew) clean
- Each packet: 4–5 PDFs + a `golden.json` with every field's expected value + planted-conflict markers.
- Eval runner CLI (`evals/runners/run.ts` or `.NET console`): loads dataset, runs the pipeline, computes metrics, writes `evals/results/<date>.json`.
- Metrics: per-field exact-match accuracy, per-doc-type breakdown. Conflict recall/precision will land in Phase 4 once LLM validators exist.
- PDF generation toolchain: pick one and commit (LaTeX, ReportLab via Python, or `pdf-lib`). Realistic-looking license/DEA/malpractice/board-cert/CV layouts.

**Gate:** `npm run eval` (or equivalent) runs against the 5 packets, prints an accuracy table, writes a results JSON. With no extractors yet, the table will be all zeros — that's fine; the harness is what's being validated.

**Status: closed 2026-05-22.** Five packets at `evals/dataset/`, four PDFs + `golden.json` each. Runner is Python (`python -m runners.run evals/dataset/`) against a P2 stub at `POST /api/extract`. `evals/results/baseline.json` committed as `stub: true` + all-zeros; the regression gate is schema-only until P3 flips `stub: false`. See [impl/phase-2-eval-harness.md](./impl/phase-2-eval-harness.md).

**Risks:**
- PDF realism. If the synthetic PDFs are too clean, accuracy claims won't transfer to real customer documents. Plant artifacts deliberately: misaligned fields, OCR noise on the scanned bucket, line wrapping on addresses.
- Golden-label correctness. Errors in golden.json silently corrupt all downstream metrics. Cross-check at least one packet manually after generation.

**Decision log seeds:**
- PDF generator choice locks Phase 4 scaling cost. Pick something scriptable enough to generate 50 in a batch.

---

## Phase 3 — Extractors + classifier

**Goal:** PDFs in → structured `ProviderProfile` out → existing Phase 1 score path produces a real (not hand-curated) score.

**Builds:**
- Document store migrations: `documents` + `document_extractions` (per §7.1).
- Append-only trigger on `document_extractions` (BEFORE UPDATE → raise).
- Upload endpoint: `POST /api/providers/{id}/documents` → stores blob (local FS or LocalStack S3) → writes documents row.
- Haiku classifier (port from `InquiryClassifier.cs`): classify each uploaded doc → write `doc_type` + confidence.
- 5 Sonnet extractors as structured-output calls: license, dea, malpractice, board_cert, cv.
- Field-location capture: bbox per field in extraction output.
- Prompt files: `prompts/license_extraction/v1.md`, etc. + `prompt_hash` written to each extraction row.
- Wire to Phase 1 score path: `/api/providers/{id}/score` now reads from extractions instead of accepting a JSON body.
- Re-run Phase 2 eval — accuracy table now populated.

**Gate:** drop the 5 Phase-2 packets into the upload endpoint, the dashboard shows real scores with real citations linking back to actual extractions. Click a citation, see the source document open at the right page.

**Risks:**
- **Vision-cost surprises.** Sonnet with vision is the highest per-call cost. Watch token usage in Langfuse; if a single packet exceeds $0.40 already, the < $0.50/intake target is in jeopardy. Mitigate by extracting once per (document × schema_version), not per turn.
- **Bbox accuracy.** Sonnet self-reports field locations; they may not align with reality on scanned docs. If accuracy is poor, fall back to highlighting the page (not the bbox) on scanned docs only.
- **OCR vs raw PDF.** Sonnet handles native PDFs and scans differently. Confirm vision input pathway is correct for both.

**Decision log seeds:**
- Object storage: local FS for now, S3 contract documented for later.
- Re-extraction policy: idempotent on (document_id, schema_version, prompt_hash). Same inputs → same `extraction_id` cached, not new rows.

---

## Phase 4 — Scale to 50 + LLM validators + published numbers

**Goal:** the README accuracy numbers exist. Cross-document reasoning works.

**Builds:**
- Programmatic packet generator: samples NPPES distributions for specialty/state/issuance-year; renders the 4 buckets (15/15/15/5).
- Add 2 LLM validators: `identity_coherence` (Sonnet, structured), `npi_taxonomy_match` (Sonnet, structured + NUCC taxonomy data).
- Add 2 remaining pure-code validators: `malpractice_currency`, `payer_specific` (YAML-driven, 2 sample payers).
- Cross-document conflict recall/precision metrics in eval runner.
- Score correlation: hand-label tier for 20 packets, compute Spearman.
- Confidence-threshold gate: Critical issues require ≥ 0.85 input confidence (per §11.1).
- `README.md` ships with the live numbers.

**Gate:** eval runs against 50 packets, README shows accuracy + conflict precision/recall + Spearman correlation. Regression suite blocks PRs that drop accuracy by > 2pp.

**Risks:**
- **Hallucinated conflicts.** `identity_coherence` may fabricate name mismatches. The 30 conflict-free packets in the eval set are the ground truth for false-positive rate. Tighten the prompt until FP rate < 5%.
- **NUCC taxonomy data freshness.** Snapshot the file; don't try to keep current.
- **Manual tier labeling effort.** 20 packets × ~5 min each = 100 min. Budget it.

**Decision log seeds:**
- Per-payer requirement YAML schema — settle before adding the third payer.
- What counts as a "conflict" for precision/recall — write the test definition before tuning the validator.

---

## Phase 5 — Intake agent + outbox

**Goal:** the full lifecycle from §6 runs end-to-end.

**Builds:**
- Domain: `IntakeSession` (renamed from VaBene `OnboardingSession`), `ProviderState` FSM.
- Port `OnboardingAgent` → `IntakeAgent` with the 5 tools from §7.4.
- Port `OnboardingTurnJob` → `IntakeTurnJob` (Hangfire, FOR UPDATE row lock).
- Port `CompleteOnboardingTool` → `ComputeReadinessTool` (terminal action pattern).
- Outbox: `outbound_messages` table + dispatcher worker with 10-min hold-at-send TTL.
- Magic-link intake portal (Next.js 15): single page, accepts uploads + 3–6 adaptive questions.
- Email backend: mock SMTP locally (MailHog or similar). Demo never hits a real provider.
- `compose_followup` tool: aggregates current gaps into one message.
- Per-provider budget cap: bound total turns. On overrun → `escalated` state + admin notification.

**Gate:** admin clicks "add provider" → magic link generated → manually open the link → upload Phase-2 packet's 4 PDFs + answer 3 questions → second turn fires → agent decides profile complete → terminal `compute_readiness` → score appears in dashboard. All visible in Langfuse as one trace.

**Risks:**
- **Turn budget tuning.** 15 steps / 80k tokens / 90s are inherited from VaBene. Watch real intake traces in Langfuse; if turns regularly hit caps, the tool surface is wrong, not the budget.
- **Consolidated-followup quality.** The `compose_followup` tool is the part that's most "vibes." Hand-review the first 20 generated followups before declaring it working.
- **Email deliverability.** Out of scope (mock SMTP), but flag in the doc that a real deploy needs DMARC/SPF.

**Decision log seeds:**
- Magic-link TTL (7 days? 14?). Pick one and document.
- Reminder cadence (default 2 reminders per `awaiting_provider` state). Configurable later.

---

## Phase 6 — Demo polish

**Goal:** the 5-minute demo records cleanly on first take.

**Builds:**
- Dashboard drill-in fully wired: PDF preview with bbox highlighting, audit-log side panel, Langfuse deep-link.
- Demo data: 3 pre-staged providers (one green/yellow/red).
- Demo script rehearsal: rehearse 3× with stopwatch.
- README hero section: accuracy numbers, comparison table from Appendix A, 60-second loom of the demo.
- `docs/demo-script.md`: shot-by-shot walkthrough (close to design doc §13 but with concrete clicks).

**Gate:** record the 5-min demo. Watch it back. If the cuts feel like cuts, redo.

**Risks:**
- Polish creep. Stop when the demo records cleanly, not when the UI is "nice."

---

## Cross-cutting risks (not phase-bound)

| Risk | Mitigation |
|---|---|
| **Extraction accuracy on scanned PDFs** is the central technical risk. Plays out across P3–P4. | Eval set's scanned bucket measures this directly. If accuracy < 80% on scanned, escalate before declaring P4 done. |
| **Synthetic packet realism**. Too-clean PDFs make the accuracy claim non-transferable to real customer data. | Plant deliberate noise: line wrap, faxed-rotation, OCR garbling. Cross-check against one or two real (de-identified) credentialing PDFs if findable. |
| **Solo build drift**. Phases that don't end on demoable gates become open-ended. | Strict gate enforcement. If a gate slips by > 1 week, cut scope, don't extend. |
| **VaBene-domain leakage during port**. Files that reference `Inquiry`/`Merchant`/`Offering` won't compile without surgery. | When porting in P0/P5, replace types eagerly. Don't stub VaBene names. |
| **Prompt-hash absence in VaBene**. The port misses this; it must be added at extraction-write time. | Phase 3 task — emit SHA256 of resolved prompt at load time, write to `document_extractions.prompt_hash`. |
| **Atano hires someone before I'm done**. | Phase 0 + Phase 1 are enough for a "here's the score logic + dashboard, here's where it goes" first-call demo. Don't gate the outreach on completing all 6 phases. |

---

## Open decisions (call before they bite)

- [ ] **Frontend stack:** Next.js 15 / React 19 (design doc default) vs a thinner Vite + TS SPA. Picking Next.js commits to App Router server components — useful for the dashboard, overkill for the intake portal. *Default: Next.js 15 for both, ship.*
- [ ] **Langfuse:** self-hosted via docker vs Langfuse Cloud free tier. *Default: self-hosted, no PHI even synthetic.*
- [ ] **Object storage:** local FS in dev, document the S3 contract. Switch in P5+. *Default: defer.*
- [ ] **NUCC taxonomy data source:** snapshot the official CSV or scrape NPI Registry. *Default: official CSV snapshot, checked in.*
- [ ] **Payer requirement YAML schema:** what fields, what required-vs-optional, how to express "depends-on-state." *Defer to P4; pick when adding payer #2.*
- [ ] **PDF generator toolchain.** Pick once in P2, can't easily change in P4. *Default: ReportLab (Python) — best layout control of the scriptable options.*
- [ ] **Eval runner language.** .NET console vs Node CLI vs Python. *Default: Python, colocated with the PDF generator. Cross-process call into the .NET extractor API.*

---

## What to defer (resist explicitly)

These are not in scope for the demo. If anything below feels tempting, that's the signal to stop.

- Real CAQH / NPPES / NPDB / OIG / SAM integrations.
- Real email delivery (DMARC, SPF, bounce handling).
- Auth beyond magic link. No SSO, no roles, no multi-tenant.
- HIPAA controls — no real PHI ever.
- Continuous re-verification jobs.
- Payer portal auto-fill.
- Mobile / responsive polish beyond "works on a laptop."
- Custom design system. Use shadcn/ui as-is.
- A11y audit. Note as a gap in README; don't fix.
- i18n. English only.
- Internal admin tooling. Score view is enough.

---

## Status

- [x] Design doc — done
- [x] Build plan — done (this doc)
- [x] Phase 0 — closed 2026-05-21, 3/4 gates green. Langfuse trace rendering deferred (Langfuse v2 has no OTLP receiver; needs v3 stack or Jaeger swap). Audit log + OTel in-process working. See [phase-0-walking-skeleton.md](./impl/phase-0-walking-skeleton.md#closing-notes-2026-05-21).
- [x] Phase 1 — closed 2026-05-21, all 7 gates green. Fixtures hit 100/62/34 exactly; side-panel drill-in wired (Radix Sheet, per-card); POST /scores returns the full ReadinessScore shape; 13 ScoreComputed audit rows landed; 139 tests pass (58 in scoring). See [phase-1-score-from-clean-input.md](./impl/phase-1-score-from-clean-input.md).
- [x] Phase 2 — closed 2026-05-22, harness end-to-end, 5 packets at `evals/dataset/`, baseline.json committed with `stub: true`. See [phase-2-eval-harness.md](./impl/phase-2-eval-harness.md).
- [x] Phase 3 — closed 2026-05-25, all 8 DoD gates green via [`apps/api/Tests/gate-walk/phase-3.sh`](../apps/api/Tests/gate-walk/phase-3.sh). Document store + blob + prompts + four Sonnet extractors + Haiku classifier + Path B intake + aggregator + score-path rewire + citation drill-in shipped across 10 slices. Eval baseline at 100% per-field across all 5 packets × 22 fields. ~$0.90 in Anthropic credits total (slice-6 baseline flip + slice-10 gate walk). See [phase-3-extractors.md](./impl/phase-3-extractors.md).
- [ ] Phase 4 — plan written + review-fixed (commits `626fe8c` → `b79f995`), not started. See [phase-4-scale-and-llm-validators.md](./impl/phase-4-scale-and-llm-validators.md).
- [ ] Phase 5
- [ ] Phase 6

---

## How to use this doc

- At the start of each phase, write `docs/impl/phase-N-<name>.md` with concrete file lists, schemas, prompts, and acceptance criteria. The build plan is the strategy; the phase docs are the tactics.
- After each phase gate, update the **Status** checklist and add a one-line note to the relevant phase about anything that diverged from plan.
- When a "Decision log seed" gets resolved, move the resolution into `docs/decisions.md` (write that file when the first decision is made).
- Open decisions get checked off as they're called. New ones get added at the bottom of the section.
