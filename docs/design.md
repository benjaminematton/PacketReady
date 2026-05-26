# PacketReady — Design Doc

> Pre-CAQH provider intake agent that ends in a cross-document packet readiness score.

| | |
|---|---|
| **Status** | Draft v1 — for review |
| **Author** | Ben Matton |
| **Audience** | Atano (Jake + eng) |
| **Date** | May 2026 |
| **Data** | synthetic only · mocked PSV · no PHI |
| **Related** | [Atano homepage](https://getatano.com/), Anthropic *Building Effective Agents* (Dec 2024) |
| **Style** | [style.md](./style.md) — voice, spine, callout vocabulary |
| **Conventions** | [conventions.md](./conventions.md) — code-side rules the language doesn't pin |

---

## 1. TL;DR

PacketReady is a vertical product slice that picks up a newly-hired provider, walks them through an adaptive email-based intake, and produces a 0–100 packet readiness score with cited remediation steps before anything is submitted to a payer. The intake half exists to feed the score half realistic, noisy provider data — not clean test fixtures.

Atano's homepage promises "approved the first time, every time" but the marketed feature list contains no pre-submission validation layer. PacketReady is the missing piece between *we collected the documents* and *we know this packet won't get denied*. The intake agent is the realistic data source; the readiness score is the product.

The build ports the architecture pattern I designed for VaBene's inquiry and onboarding agents (multi-tool agent loop, per-turn state persistence, outbox-pattern outbound, two-phase commit on terminal actions, Langfuse-style audit trail) into the credentialing domain. Every piece of agent behavior is logged, cited, and reviewable.

---

## 2. Background

### 2.1 The gap

Atano markets five primitives: document intelligence, primary source verification, payer auto-fill, real-time reporting, and smart follow-ups. None of them is *cross-document validation against payer-readiness criteria*. The closest is the bottom-line promise — "ensure providers get approved the first time, every time" — but a homepage promise without a feature behind it is the seam I'm targeting.

Reviewing competitors:

- **Verifiable** ships CredAgent which begins at "Getting CAQH data" — the workflow before CAQH exists is not their problem.
- **Assured** markets "Pre-Submission Error Detection vs. Blind Submission" but ties it to CAQH+NPPES auto-fill, not cross-document reasoning.
- **Medallion** assumes enterprise customers with existing provider-data infrastructure.

Atano's ICP — small practices and RCMs — frequently onboards providers who aren't in CAQH yet, don't have a CAQH-linked Salesforce instance, and don't have an existing provider-data warehouse. The intake-to-readiness path I'm building is shaped for that ICP specifically.

### 2.2 Why this combination

The intake agent alone recomposes things Atano already markets (extraction, follow-ups, dashboard view of status). The readiness score alone reduces to a schema check on a clean dict. Together they're one coherent product slice: intake produces the realistic noisy data that the readiness score has to reason over, and the readiness score gives intake a destination that isn't "fields collected."

The readiness score is also where the LLM does work a regex chain can't. Identity mismatches across documents, address variants, ambiguous date formats on scanned PDFs, taxonomy-to-specialty mapping, payer-specific requirement gaps — these need cross-document reasoning, not field-level validation.

### 2.3 The denial economics

A first-time denial restarts a 90–120 day enrollment cycle and costs a small practice an estimated $6–8K per provider per month in lost billing. The Packet Readiness Score is the feature that prevents the loss before submission, which is the part of Atano's pitch the rest of their feature surface hasn't built.

---

## 3. Goals

1. **Adaptive intake works without CAQH.** Provider receives a single magic link, completes the intake in 1–3 turns over email, never sees a 47-field form.
2. **Consolidated follow-ups, not template reminders.** When information is missing, the agent batches all gaps into one targeted email rather than firing a generic reminder for each missing field.
3. **A 0–100 readiness score with three severity tiers** (Critical / Major / Minor), each issue carrying a cited remediation step pointing to the source document and the specific text that triggered it.
4. **Cross-document reasoning surfaces real conflicts.** Name mismatches, date conflicts, taxonomy-specialty mismatches, expired credentials, missing payer-specific docs — caught at the score layer, not at submission.
5. **Every agent decision is auditable.** A reviewer can click any issue and see the LLM prompt, tool call, source document page, and primary-source citation that produced it.
6. **An eval harness backs the accuracy claim.** 50 synthetic packets with golden labels; per-field extraction accuracy reported in the README; regression suite runnable on prompt or model changes.

### 3.1 Measurable targets

| Metric | Target |
|---|---|
| Extraction field accuracy (clean PDFs) | ≥ 95% |
| Extraction field accuracy (scanned PDFs) | ≥ 85% |
| Tier agreement with human-labeled readiness (weighted Cohen's κ, quadratic weights) | ≥ 0.50 on the eval set; 3×3 confusion matrix + raw agreement reported alongside |
| Cross-document conflict recall | ≥ 90% on synthetic conflicts |
| End-to-end intake turn latency (p50) | < 30s after document upload |
| End-to-end intake turn latency (p95) | < 90s |
| Cost per provider intake (model spend) | < $0.50 |

---

## 4. Non-goals

- **Real CAQH ProView API integration.** It's paid and org-only. I'll model the CAQH-fallback path and write a mock client; the demo will not pull live data.
- **Real primary-source verification calls.** NPPES, NPDB, OIG, SAM, state medical boards each need separate integrations. I'll mock the verification layer and document the contract; live PSV is out of scope.
- **Auto-filling payer portals.** Atano already markets this. Reproducing it doesn't differentiate.
- **A production-grade authn/authz model.** Magic-link auth is sufficient for the demo. SSO, RBAC, multi-tenant isolation: out.
- **HIPAA-compliant deployment.** Synthetic data only. Real PHI handling is a separate uplift.
- **Continuous monitoring.** A scheduled re-verification loop is a clean follow-on but not part of this slice.
- **A polished UI.** The dashboard view exists to make the score legible. Visual polish is not the artifact.

---

## 5. System overview

```
┌────────────────────────────────────────────────────────────────┐
│                       PacketReady System                       │
├────────────────────────────────────────────────────────────────┤
│                                                                │
│  ┌──────────┐    ┌────────────┐    ┌────────────────────────┐  │
│  │  Admin   │───▶│  Provider  │───▶│  Provider (next turn)  │  │
│  │ (kicks   │    │  (magic    │    │  (responds to followup)│  │
│  │  off)    │    │  link)     │    │                        │  │
│  └──────────┘    └─────┬──────┘    └────────────┬───────────┘  │
│                        │                        │              │
│                        ▼                        ▼              │
│                  ┌──────────────────────────────────┐          │
│                  │      Intake State Machine        │          │
│                  │  (orchestrator, per-turn agent)  │          │
│                  └──┬─────────┬─────────┬───────────┘          │
│                     │         │         │                      │
│           ┌─────────▼──┐  ┌───▼──────┐ ┌▼────────────┐         │
│           │ Extraction │  │ Outbound │ │  Audit Log  │         │
│           │   Layer    │  │ Outbox   │ │  (Langfuse  │         │
│           │ (per-doc)  │  │ (1 msg)  │ │  + JSONL)   │         │
│           └─────────┬──┘  └──────────┘ └─────────────┘         │
│                     │                                          │
│                     ▼                                          │
│           ┌─────────────────────┐                              │
│           │  Document Store     │                              │
│           │  (append-only,      │                              │
│           │  versioned, JSONB)  │                              │
│           └─────────┬───────────┘                              │
│                     │                                          │
│                     ▼                                          │
│           ┌─────────────────────────────────────────────────┐  │
│           │              Validator Suite                    │  │
│           │  identity · license · DEA · malpractice ·       │  │
│           │  NPI/taxonomy · payer-specific · sanctions      │  │
│           └─────────────────────────┬───────────────────────┘  │
│                                     │                          │
│                                     ▼                          │
│                       ┌─────────────────────────┐              │
│                       │  Score Synthesis        │              │
│                       │  0–100 + Critical/      │              │
│                       │  Major/Minor + cites    │              │
│                       └─────────────┬───────────┘              │
│                                     │                          │
│                                     ▼                          │
│                       ┌─────────────────────────┐              │
│                       │   Dashboard view        │              │
│                       │   (drill-in per issue)  │              │
│                       └─────────────────────────┘              │
└────────────────────────────────────────────────────────────────┘
```

The system breaks into seven subsystems. I'm explicitly modeling this after Anthropic's *Building Effective Agents* framework: the intake half is an **orchestrator–workers** workflow (intake state machine routes per-turn work to extraction workers), and the readiness half is an **evaluator–optimizer** workflow (specialized validators feed a synthesis step). Neither half is a fully autonomous agent loop — every state transition is governed by deterministic code, and the LLM is the augmented step inside each transition.

This is a deliberate design choice. Fully autonomous agents are appropriate when the task is open-ended. Credentialing intake has a known target shape (a complete provider profile against a known requirement set), so a workflow with embedded LLM calls is the right pattern and the more debuggable one. Cite: Anthropic's guidance is to start with the simplest pattern that works and only add autonomy when the task can't be enumerated.

---

## 6. Provider lifecycle

The happy-path flow, end-to-end:

```
T+0      Admin enters new hire (name, email, role, specialty)
         → System creates Provider record (status: intake.pending)
         → Generates magic link, drops job in outbound outbox

T+5min   Outbox worker sends intake email to provider
         → Email contains link + brief explainer + list of docs to have ready

T+1day   Provider clicks link
         → Lands on minimal upload page (no 47-field form)
         → Uploads documents they have on hand
         → Sees extracted-field cards with source citations,
           confirms or edits each value before commit (§7.9)
         → Answers a short adaptive form (3–6 questions tailored
           to what's missing after extraction)
         → Submits

T+1day   Intake agent turn-1 fires:
           1. Classify each uploaded doc (Haiku)
           2. Extract fields from each (Sonnet, structured output)
           3. Update Provider profile (versioned write)
           4. Run preliminary validators on what we have
           5. Compute gap list: what's still missing or conflicting
           6. Decide: terminal (run readiness) or continue (one consolidated followup)
           → Persists turn artifact, queues outbound message

T+1day   If continue: provider receives ONE followup email
         listing all gaps in priority order with examples of
         what's needed. Magic link is re-usable.

T+2day   Provider responds (uploads more / answers more)
         → Intake agent turn-2 fires, same loop

T+2day   When agent decides "we have enough to score":
           1. Mark profile complete, kick to readiness pipeline
           2. Run all validators in parallel
           3. Synthesize score (0–100, Critical/Major/Minor breakdown)
           4. Generate cited remediation for every non-passing issue
           5. Notify admin: "Provider X ready for review, score 87/100"

T+2day   Admin opens dashboard, sees the score, drills into
         each issue, clicks through to source document and
         primary-source citation.
```

The agent has a per-provider budget cap that bounds total turns. If the budget exhausts before the agent decides we have enough to score, the system escalates to a human (admin gets a "this provider needs hands-on review" notification with the partial state attached).

---

## 7. Detailed design

### 7.1 Document store

Append-only, versioned, JSONB-backed. Mirrors the Persistence subsystem I designed for VaBene.

```sql
CREATE TABLE documents (
  id              UUID PRIMARY KEY,
  provider_id     UUID NOT NULL REFERENCES providers(id),
  doc_type        TEXT,            -- inferred: license | dea | malpractice | board_cert | cv | other
  doc_type_conf   FLOAT,           -- classifier confidence (Haiku output)
  storage_uri     TEXT NOT NULL,   -- S3-like blob ref
  original_name   TEXT,
  mime_type       TEXT,
  page_count      INT,
  uploaded_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  uploaded_by     TEXT             -- 'provider' | 'admin'
);

CREATE TABLE document_extractions (
  id              UUID PRIMARY KEY,
  document_id     UUID NOT NULL REFERENCES documents(id),
  extraction_id   INT NOT NULL,    -- monotonic per document_id
  schema_version  TEXT NOT NULL,   -- e.g. 'license.v2'
  fields          JSONB NOT NULL,  -- the field map for this row (raw on initial extraction, corrected on subsequent edit rows)
  field_locations JSONB NOT NULL,  -- { field_name: { page: int, bbox: [...] } } for citation
  source          TEXT NOT NULL,   -- 'llm' | 'provider_edit' | 'admin_edit'  (§7.9)
  edited_by       UUID,            -- user id when source != 'llm'           (§7.9)
  model           TEXT,            -- claude-sonnet-4-6 (null when source != 'llm')
  prompt_hash     TEXT,            -- hash of prompt template + version (null when source != 'llm')
  extracted_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  confirmed_at    TIMESTAMPTZ,     -- null until provider/admin confirms (§7.9); validators read the latest confirmed row
  UNIQUE (document_id, extraction_id)
);
```

**Why append-only:** every extraction is preserved. When a prompt change improves accuracy, I can re-extract and compare against the previous extraction without losing history. When a customer asks "why did the score change," the audit trail traces back to the specific extraction the score was computed against.

**Why `field_locations`:** every cited issue in the final score must link to a specific page and bbox in the source document. Without storing locations at extraction time, citations would require re-running extraction at score time. Cheap to store, expensive to recompute.

### 7.2 Extraction layer

One extractor per document type. Each extractor is a structured-output Claude Sonnet call with a JSON schema. Document classification (which extractor to invoke) is a Claude Haiku call.

**Why Haiku for classification:** the task is a single short LLM call returning a label. Sonnet is overkill. Haiku at ~6x lower cost and ~2x lower latency is the right pick. This is the same split I'm using in VaBene's inquiry agent (Haiku for inquiry classification, Sonnet for the agent loop).

**Why Sonnet for extraction:** field extraction from credentialing PDFs has long tails — multi-line addresses, ambiguous date formats, faxed documents with OCR artifacts, board cert numbers that look like license numbers. Sonnet's reasoning matters on the edge cases. Haiku struggles with the harder documents in early testing.

Extractors return:

```typescript
type LicenseExtraction = {
  full_name: { value: string; page: number; bbox: [x,y,w,h] };
  license_number: { value: string; page: number; bbox: [...] };
  license_state: { value: string; page: number; bbox: [...] };
  issue_date: { value: string; page: number; bbox: [...] };  // ISO 8601
  expiry_date: { value: string; page: number; bbox: [...] };
  status: { value: 'active' | 'suspended' | 'expired' | 'unknown'; page: number; bbox: [...] };
  // ...
  _confidence: Record<string, number>;  // per-field self-reported confidence
};
```

Per-field self-reported confidence is used by the validator suite to weight cross-document checks. Low-confidence extractions get flagged as Minor issues even when they "look" valid — confidence ≥ 0.85 to count as a passing input to a Critical-eligible validator.

**Classifier confidence — three-band runtime handling.** The Haiku classifier emits a per-document confidence alongside the predicted `doc_type`. Three bands govern what happens next:

| Band | Action |
|---|---|
| ≥ 0.85 | Trust. Route to the matching extractor; no Issue emitted on classification. |
| 0.50–0.85 | Store the predicted `doc_type` and proceed to extraction, but the validator suite emits a Minor "low-confidence classification" Issue at score time so the admin sees the uncertainty. |
| < 0.50 | Persist as `doc_type = 'other'`. The aggregator skips the document. A Critical "unclassified document" Issue surfaces unless another document of the expected type was uploaded for the same provider. |

The ≥ 0.85 bench is on the synthetic eval set; runtime PDFs from real intake will be messier. The three-band split keeps the system honest about its own uncertainty rather than collapsing every classification into "trusted."

**Extractor count for v1.** Four extractors ship in the first cut — `license`, `dea`, `board_cert`, `malpractice` — matching the four document types in the eval dataset. The `cv` extractor lands once a CV-bearing packet enters the eval set; shipping it without a regression target would invite silent bit rot.

### 7.3 Intake state machine

The state machine is a five-state FSM owning the per-provider lifecycle. Transitions are deterministic; the LLM lives inside the `agent_turn` action, not in the transition logic.

```
states: pending → awaiting_provider → agent_processing → awaiting_provider → ... → complete
                                                                                 → escalated
```

```typescript
type ProviderState =
  | { kind: 'pending'; created_at: Date }
  | { kind: 'awaiting_provider'; magic_link_id: string; reminder_sent_count: 0 | 1 | 2 }
  | { kind: 'agent_processing'; turn_id: string; started_at: Date }
  | { kind: 'complete'; readiness_score_id: string; completed_at: Date }
  | { kind: 'escalated'; reason: string; partial_profile: ProviderProfile };
```

Each `agent_turn` is a single agent loop invocation with a strict budget:

| Budget | Per turn |
|---|---|
| Steps | 15 |
| Tokens | 80,000 |
| Wall clock | 90s |

These mirror the budgets I'm using on VaBene. They're chosen so a runaway loop costs less than $0.20 worst case. Exceeding any of them aborts the turn, preserves partial state, and transitions to `escalated`. The admin gets a notification with the partial trace attached.

**Why a state machine and not a free-running agent loop:** the credentialing intake task has a known target shape (a complete profile against a known requirement set), so the orchestration is deterministic and the LLM lives inside the orchestration. Per Anthropic's framework, this is the orchestrator–workers workflow pattern, not an autonomous agent. The benefits I care about: every state transition is debuggable, mid-turn failures don't lose state, and the agent can't decide to "do something else" mid-intake.

### 7.4 Agent runtime — tools

**Two endpoint surfaces for extraction.** The extraction tool surface and the HTTP surface diverge by use case:

- `POST /api/extract` is **stateless** — accepts a PDF + `docType`, returns `{ fields }`, writes nothing. Used by the eval runner (which has the PDFs on disk and already knows `docType` from `golden.json`). No `documents` row, no idempotency cache, no classifier call.
- `POST /api/providers/{id}/documents` is **stateful** — uploads the blob, persists a `documents` row, runs the Haiku classifier inline, runs the Sonnet extractor inline, persists a `document_extractions` row. Idempotent on `(document_id, schema_version, model, prompt_hash)` so a re-extract against the same prompt + model returns the cached row without re-billing Sonnet. This is the intake path.

The `extract_fields` agent tool below maps to the stateful surface — by the time the agent is reasoning about a document, it has a `document_id` and the persistence trail is load-bearing for audit.

The intake agent has five tools. Tool surface kept deliberately small because every extra tool increases miss-selection rate (this is the *Building Effective Agents* "tool inventory" principle — minimum set needed for the top tasks):

```typescript
// Read a document and its current extraction
read_document(document_id: string): {
  doc_type: string;
  extracted_fields: object;
  field_locations: object;
};

// Extract fields from a document against a schema
extract_fields(document_id: string, schema: 'license' | 'dea' | 'malpractice' | 'board_cert' | 'cv'): Extraction;

// Look up a provider in primary sources (mocked in demo)
lookup_primary_source(source: 'nppes' | 'oig' | 'sam' | 'state_board', identifiers: object): LookupResult;

// Compose the next outbound message based on current gaps
compose_followup(provider_id: string, gaps: Gap[]): { subject: string; body: string };

// TERMINAL: profile is complete enough to score
compute_readiness(provider_id: string): { score: number; issues: Issue[]; computed_at: Date };
```

**Terminal action pattern:** `compute_readiness` is a terminal marker. When the agent invokes it, the state machine transitions out of `agent_processing` and into the readiness pipeline. This mirrors VaBene's `compose_response` terminal pattern — a single tool call ends the agent loop and hands control back to the orchestrator. This avoids the "agent keeps looping when it should have stopped" failure mode.

**Why no `send_email` tool:** the agent composes messages but never sends. Sending is the outbox subsystem's job. Same separation I use in VaBene's onboarding agent — the LLM proposes outbound content; deterministic code commits and sends. This prevents the agent from accidentally double-sending and makes the dispatch layer independently testable.

### 7.5 Outbound messaging — outbox pattern

All outbound provider messages go through an outbox table. The intake agent writes a message proposal; a background worker reads, deduplicates against recent sends, and dispatches.

```sql
CREATE TABLE outbound_messages (
  id           UUID PRIMARY KEY,
  provider_id  UUID NOT NULL REFERENCES providers(id),
  turn_id      UUID NOT NULL,
  kind         TEXT NOT NULL,  -- 'intake_invitation' | 'followup' | 'completion_notice'
  subject      TEXT NOT NULL,
  body         TEXT NOT NULL,
  status       TEXT NOT NULL,  -- 'queued' | 'held' | 'sent' | 'cancelled'
  held_until   TIMESTAMPTZ,    -- 10-minute hold-at-send TTL
  composed_at  TIMESTAMPTZ NOT NULL,
  sent_at      TIMESTAMPTZ
);
```

**Why a hold-at-send TTL:** a 10-minute hold lets an admin yank a misfired message before it goes out. This is straight from VaBene's Approval and Send subsystem. For credentialing it matters more, not less — provider trust is fragile and a confused followup costs intake completion rates.

**Why consolidated follow-ups (not template reminders):** the agent batches all current gaps into one targeted email. "We're missing your DEA expiry date, the second page of your malpractice face sheet, and confirmation of your specialty board cert" instead of three separate template reminders fired by a cron. This is genuinely better UX than Atano's current "Smart Follow-ups" feature, which from the homepage description appears to be schedule-based reminders. One email, all gaps, one click to resume.

### 7.6 Validator suite

Validators run in parallel after the agent declares the profile complete. Each validator is a focused module — some pure code, some LLM-augmented — that owns one validation dimension. Inspired by Anthropic's evaluator–optimizer pattern.

**Validators in the v1 build:**

| Validator | What it checks | Implementation |
|---|---|---|
| `identity_coherence` | Name, DOB, NPI, address agree across all documents | LLM-augmented (Sonnet, structured) — needs cross-doc reasoning |
| `license_status` | License is active, not expired, state matches credentialing state | Pure code on extracted fields |
| `dea_status` | DEA registration active, not expired, schedules cover prescribing needs | Pure code |
| `malpractice_currency` | Policy in force, coverage limits ≥ payer minimums (`Provider.PayerId` → YAML), expiry > 30 days out | Pure code + per-payer YAML |
| `npi_taxonomy_match` | NPI taxonomy code matches stated specialty | LLM-augmented (taxonomy lookup is fuzzy) |
| `board_certification` | Board cert active for stated specialty, not expired; payer-aware accepted-boards list when `Provider.PayerId` configures one | Pure code, payer-aware via YAML |
| `sanctions_check` | OIG/SAM lookup result is clean | Pure code on lookup result |
| `required_documents` | Each doc type listed in the payer's `requiredDocuments` is present on the provider | Pure code, per-payer YAML |

Each validator returns:

```typescript
type ValidatorResult = {
  validator: string;
  status: 'pass' | 'minor' | 'major' | 'critical';
  message: string;
  citations: Citation[];  // [{ document_id, page, bbox, extracted_value }]
  remediation: string;
};
```

**Why split pure-code from LLM-augmented:** field-level checks (expiry > today, status === 'active') are deterministic and shouldn't burn LLM budget. Cross-document reasoning (does "Jonathan Smith" on the license match "John Smith Jr." on the malpractice cert with the same DOB?) needs reasoning. Splitting the validator surface keeps cost down and makes the LLM-required portions debuggable in isolation.

**Why parallel:** validators don't depend on each other's output. A single fan-out runs all of them in ~1–2 seconds with cached extractions.

**Per-provider payer assignment.** Three validators above are payer-aware (`malpractice_currency`, `board_certification`, `required_documents`). Each `Provider` carries a `PayerId` (TEXT, defaults to `'payer-a-national-hmo'` at creation) that selects the YAML config at `Infrastructure/Payers/payers/<id>.yaml`. The admin sets `PayerId` at intake; the seed CLI varies it across fixtures so both YAML branches exercise. Missing-payer references fail loud at startup, not on first request — `PayerRequirementLoader` rejects an unresolved `PayerId`.

### 7.7 Score synthesis

The synthesis step takes the validator results and produces:

```typescript
type ReadinessScore = {
  score: 0..100;
  tier: 'green' | 'yellow' | 'red';  // ≥85 / 60–84 / <60
  breakdown: {
    critical_count: number;
    major_count: number;
    minor_count: number;
  };
  issues: Issue[];  // sorted by severity, then by validator confidence
  computed_at: Date;
  inputs: { extraction_ids: string[]; validator_versions: object };
};
```

**Why 0–100 and not a letter grade:** numeric scoring lets admins sort and filter ("show me all providers under 80"). A letter grade collapses the same information into less actionable detail. The tradeoff is that the specific 0–100 number has to mean something defensible — see the scoring rubric below.

**Scoring rubric:**

```
Start at 100.
For each Critical issue:  -25
For each Major issue:     -10
For each Minor issue:     -3
Floor at 0.
```

This is intentionally simple. A weighted sum is easier to defend than a learned score in an interview setting and easier to debug when an admin asks "why is this provider at 73?"

**Why this rubric:** the absolute number matters less than the categorical tier and the ordering. A weighted sum produces a stable ordering, a defensible explanation per provider, and lets the demo show "73 = one Critical (NPI taxonomy mismatch) + one Minor (low-confidence DOB extraction)."

### 7.8 Audit log

Every agent decision, tool call, validator result, and score computation is logged across two surfaces with distinct purposes:

- **`audit_events` (Postgres, append-only)** — the durable source of truth. DB-enforced via `BEFORE UPDATE → RAISE` trigger. Every state-changing event lands here with `provider_id`, `turn_id`, `event_type`, JSONB payload, and `correlation_id`. This is what the dashboard's drill-in reads from and what NCQA-style audit-package exports would be generated from.
- **Langfuse** — the trace UI. Per-turn spans, model calls, tool calls, latencies, costs. Best-effort fire-and-forget; an unreachable Langfuse never breaks a request. (Note: the local docker stack runs Langfuse v2, which has no OTLP receiver — traces emit but don't render. The v3-vs-Jaeger decision is parked for P4 when trace search becomes load-bearing for eval debugging.)

```typescript
type AuditEvent = {
  id: string;
  provider_id: string;
  turn_id?: string;
  ts: Date;
  event_type: string;  // 'PingExecuted' | 'ScoreComputed' | 'ExtractionPersisted' | ...
  payload: object;     // JSONB
  correlation_id?: string;
};
```

The dashboard's "drill into this issue" UX is backed by `audit_events` joined to `document_extractions.field_locations`. Every Critical/Major issue has a clickable card that opens a side panel showing: the validator that fired, the extracted value that triggered it, the source document with the cited page rendered and the bbox highlighted, and the primary-source link if the issue involved an external lookup.

This is the cited-audit-log pattern Verifiable markets for CredAgent. Atano's homepage does not promise this. Building it is a strong differentiator and is also genuinely the right way to build credentialing software — the alternative is asking customers to trust a black box, which is the failure mode that produced the 2026 NCQA audit problems.

### 7.9 Error correction and state recovery

Extraction is the place a credentialing system is most likely to be wrong, and "the agent silently committed a bad value" is the failure mode that destroys provider trust. The intake flow has four layers of correction, ordered from cheapest to most invasive.

**1. Confirmation-before-commit (per extraction).** When an extractor returns, it writes a `document_extractions` row with `source='llm'` and `confirmed_at=null`. The provider sees a card listing each extracted field with the source page rendered and the bbox highlighted. Clicking "looks right" stamps `confirmed_at` on that row. Editing any field appends a new row (`source='provider_edit'`, monotonically next `extraction_id`, `confirmed_at` set, `model`/`prompt_hash` null) — the LLM's original output is preserved as the prior row. Validators only consume the latest row per `document_id` where `confirmed_at IS NOT NULL`.

**Why on-card and not after-the-fact:** the audit log (§7.8) is sufficient to prove what we did, but doesn't catch errors before they propagate. Confirmation-before-commit catches the long-tail extraction errors (faxed dates, ambiguous middle initials, OCR-mangled license numbers) at the cheapest possible point — before the score has been computed against them and before a downstream payer call has been made.

**2. Field-level edit with cascading validator re-run.** After commit, any field is editable from the dashboard — provider on their own packet, admin on any packet. An edit appends a new `document_extractions` row (monotonic `extraction_id + 1`, `source='provider_edit'` or `'admin_edit'`, `edited_by` recorded) and triggers a targeted validator re-run: only validators whose input set intersects the edited field re-execute; everything else stays cached. The dependency map (validator → fields it reads) is declared in code and is the source of truth.

**Why partial re-run:** validators are independent (§7.6), so cascading is correct and ~10x cheaper than re-fanning all eight. A name edit on a license doc shouldn't force a malpractice-currency check.

**3. Per-turn checkpoint (the substrate).** This layer is already provided by §7.1 + §7.3; this section just names the contract. Every `agent_turn` writes atomically across `document_extractions`, `outbound_messages`, and the audit log, keyed by `turn_id`. The turn artifact is the rewind unit. Any later turn is reachable from any prior turn's snapshot.

**4. Document-level time-travel (full back-out).** When the provider says "wrong document, scrap that one," the system:

1. Marks the offending `documents` row as superseded (append-only — nothing is deleted).
2. Rewinds the FSM to the turn artifact immediately before the bad upload.
3. Replays forward. Steps that don't depend on the superseded document reuse cached outputs; steps that do are re-executed with the corrected inputs.
4. Re-synthesizes the readiness score against the new state; the old score is preserved with an `invalidated_at` marker.

Scope: the provider can rewind their own packet through the confirmation card and the dashboard. Admin holds the time-travel power for already-submitted packets, since a rewind re-issues notifications and a misfired rewind is destructive in the same way a misfired email is.

**Compliance.** NCQA's 2026 credentialing standard requires the credentialing file maintain an audit trail of changes — actor, prior value, new value, timestamp, reason. This design satisfies that requirement by construction: every confirmation, edit, and rewind appends to the audit log (§7.8) with full correlation. Score rows are preserved with `invalidated_at` rather than overwritten. The system can produce the change history of any field on demand.

**Replay safety for side-effecting tools.** The runtime re-runs validators and extraction on rewind by design — those are deterministic given the same inputs. `lookup_primary_source` (§7.4) is not deterministic in the right way: NPPES is free, but a live CAQH or state-board call costs money per query and is logged on the payer side. Re-executing one on replay is wrong even if the LLM regenerates an identical request, because the duplicate call shows up in the payer's audit trail. The policy:

- Every `lookup_primary_source` invocation writes a `primary_source_results` row keyed by `(source, identifiers_hash)`.
- On replay, the runtime checks the cache against the rewound state's identifiers. Cache hit → reuse the stored result. Cache miss (because the corrected extraction actually changed an NPI) → explicitly re-execute on a new branch and log the divergence.
- The runtime never silently re-executes a side-effecting tool after rewind.

```sql
CREATE TABLE primary_source_results (
  id                UUID PRIMARY KEY,
  source            TEXT NOT NULL,        -- 'nppes' | 'oig' | 'sam' | 'state_board' | 'caqh'
  identifiers       JSONB NOT NULL,       -- the canonicalized input identifiers (npi, license #, state, etc.)
  identifiers_hash  TEXT NOT NULL,        -- SHA256 of the canonicalized identifiers; cache key
  result            JSONB NOT NULL,       -- the lookup response payload
  status            TEXT NOT NULL,        -- 'ok' | 'not_found' | 'error'
  turn_id           UUID,                 -- the turn that triggered the first call
  requested_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  UNIQUE (source, identifiers_hash)
);
```

The UNIQUE constraint is the cache. `identifiers_hash` is computed by canonicalizing the input JSON (sorted keys, normalized casing, trimmed whitespace) before hashing — otherwise the LLM regenerating the request with reordered keys would defeat the cache.

This is a known failure mode in checkpoint-restore systems for LLM agents: replayed tool calls are not guaranteed to be byte-identical because the model re-synthesizes the request, so any tool that costs money or hits external state needs the cache-or-fork policy, not blind replay.

**Reuse and new construction.** Layer 3 is existing infrastructure (Appendix C.1, C.3). Layers 1, 2, 4 and the replay-safety policy are new construction. The building blocks (immutable extractions, turn-keyed audit events, deterministic validators) are existing pieces composed differently.

---

## 8. Model and prompt strategy

| Layer | Model | Why |
|---|---|---|
| Document classification | Claude Haiku 4.5 | Cheap, fast, single-label task |
| Field extraction | Claude Sonnet 4.6 | Long-tail edge cases need reasoning |
| Identity coherence | Claude Sonnet 4.6 | Cross-document fuzzy matching |
| NPI/taxonomy match | Claude Sonnet 4.6 | Fuzzy specialty mapping |
| Outbound message composition | Claude Sonnet 4.6 | Tone matters; consolidation matters |
| Other validators | None (pure code) | Deterministic |

**Prompt versioning:** every prompt is a versioned file (`prompts/license_extraction/v3.md`). Extraction records carry the prompt hash. Eval results are tagged with the prompt version. This is non-negotiable for a system whose outputs feed a downstream score — you cannot debug score drift without prompt version history.

**Structured outputs:** all LLM calls in production paths use structured output (JSON schema constraint). No free-form parsing.

---

## 9. Eval plan

The accuracy claims need backing or they're vibes. The eval harness is a first-class part of the build, not a follow-on.

### 9.1 Synthetic dataset

50 synthetic provider packets, each containing 3–6 documents. Generated by:

1. Sampling realistic provider profiles (specialty, state, license issuance year, etc.) from public NPPES distributions.
2. Programmatically generating PDFs for each document type with realistic layouts, then introducing controlled noise:
   - 15 packets: clean PDFs, no conflicts (sanity)
   - 15 packets: clean PDFs, with planted cross-document conflicts (name variants, DOB mismatches, expiry conflicts)
   - 15 packets: scanned-style PDFs (rasterized + slight rotation/skew) with no conflicts
   - 5 packets: scanned + conflicts (the hard tail)

Every field in every document has a golden label. Every planted conflict has a golden expected-Critical entry.

### 9.2 Metrics

```
per-field extraction:
  - exact-match accuracy
  - field-level precision and recall
  - per-document-type breakdown

cross-document conflict detection:
  - conflict recall (we caught the planted issue)
  - conflict precision (we didn't fabricate a conflict on clean packets)

tier agreement:
  - weighted Cohen's κ (quadratic weights) between PacketReady tier and a human-rated readiness tier (headline)
  - 3×3 confusion matrix (rows = human tier, cols = system tier)
  - raw agreement rate (count(system == human) / n)
  - Spearman correlation kept as a footnote — 3-tier categorical labels at n=20 make ρ heavy-tied and unstable; κ is the standard ordinal-categorical agreement metric
```

### 9.3 Regression suite

The eval set runs end-to-end on every prompt change, model change, or validator change. Numbers are checked into the repo under `evals/results/`. If accuracy drops more than 2 percentage points on any per-field metric, the change does not ship.

### 9.4 What the README shows

The README publishes per-field accuracy numbers, the score correlation, and the conflict-detection precision/recall. This is the marketing copy Atano doesn't currently have on their own site — and it's the answer to "how do you know it works."

---

## 10. Alternatives considered

### 10.1 Build the readiness score alone, mock the input

I considered skipping the intake half and just building the score against fixed structured inputs. **Rejected** because the score's hardest claim — cross-document reasoning over noisy real extractions — gets undercut when the input is a clean dict. A reviewer would correctly ask "but does this work on actual extracted data?" and the demo would have no answer.

### 10.2 Build the intake agent alone, no scoring

This was my original pick. **Rejected after pressure-testing** because Atano markets every primitive an intake agent composes (extraction, follow-ups, dashboard). The marginal value over Atano's current product is small, and "I built a less-featured version of Verifiable" is the worst-case interpretation. The intake-without-score build optimizes for the wrong signal.

### 10.3 Build a fully autonomous agent loop, no state machine

I considered letting the agent dynamically decide its own flow per provider. **Rejected** because credentialing intake has a known target shape and well-understood failure modes. Anthropic's guidance is to start with the simplest pattern that works — workflows beat agents when the task is enumerable, and intake-to-score is enumerable.

### 10.4 Real CAQH ProView integration via mock

I considered building a fake CAQH ProView API as a separate service to demo the CAQH fallback path. **Rejected** because the engineering cost is high (mocking the schema correctly) and the demo signal is low (it's just an API call). Instead, the design documents the contract — `lookup_primary_source('caqh', { npi })` returns a typed `CaqhProfile | NotFound` — and the demo uses the email-first path.

### 10.5 Browser-driven payer portal auto-fill

I considered an end-of-flow "submit to payer" demo where the score, if green, auto-fills a payer portal via Playwright. **Rejected** because Atano markets this and competing on it doesn't differentiate. The flow ends at "packet is ready to submit," which is the part of the pitch their current product surface doesn't deliver.

---

## 11. Risks

1. **Extraction accuracy on scanned documents.** Sonnet's vision is strong but not perfect on faxed, slightly rotated PDFs. Mitigation: the eval set includes scanned variants explicitly, and the validator suite weights low-confidence extractions down rather than treating them as ground truth. Critical Issues whose citations reference any field with confidence < 0.85 are auto-downgraded to Minor via a structural `IsLowConfidenceInput: true` flag on the `Issue` (mirrored on `Citation.LowConfidence`) — not a message suffix, so the dashboard and tests can branch on the flag without string-sniffing.

2. **Hallucinated conflicts.** The identity coherence validator could fabricate a conflict (LLM "sees" a name mismatch that isn't there). Mitigation: every claimed conflict carries a citation; the eval set's clean-packet portion measures false-positive rate explicitly.

3. **Validator coverage gaps.** Payer-specific requirements vary; I'm implementing a configurable subset, not full coverage. Mitigation: the payer-aware validators (`required_documents`, `malpractice_currency`, `board_certification`) all read from a per-payer YAML file, so adding a payer is config, not code.

4. **The 0–100 score as a UX decision.** Some reviewers may prefer a checklist. Mitigation: the score is paired with the tier and the issue list, so the consumer can use whichever framing they want. The number is sortable; the issue list is actionable.

---

## 12. Open questions

1. How do you (Atano) currently handle providers that aren't in CAQH? Is the email-first intake a path you've thought about, or is the implicit assumption that customers have CAQH-ready providers?
2. Is your "Smart Follow-ups" feature template-based reminders or agent-composed? The consolidated-followup pattern is a real refinement if it's the former.
3. What does your audit trail look like today? Is decision provenance per-issue something your customers ask for, or is the current dashboard sufficient?
4. Per-payer requirement configuration — do you maintain this internally, or do customers configure it? Affects whether the YAML-per-payer pattern is the right shape.
5. NCQA audit-package export — is this on your roadmap? The audit log subsystem here is designed to support it as a follow-on.

---

## 13. Demo script (5 minutes)

1. **Open on the score, not the intake (30s).** Dashboard shows a provider list with scores. Click into "Dr. Lee — 73/100, yellow." The issue panel shows: 1 Critical (NPI taxonomy mismatch — taxonomy 207Q00000X listed but board cert is in Cardiology), 1 Major (malpractice expiry in 18 days), 2 Minor.

2. **Drill into the Critical (60s).** Click the NPI mismatch issue. Side panel opens: shows the extracted taxonomy from the CAQH-shaped profile, shows the board cert PDF with the cardiology certification highlighted, shows the NPPES lookup result. The cross-document mismatch is the headline.

3. **Show the audit trail (45s).** Click "Why did we flag this?" Audit log opens: classifier call (Haiku, $0.0003), extraction call (Sonnet, $0.02), `identity_coherence` validator invocation, score synthesis. Every step has a timestamp, model, prompt version, and a clickable link to the source extraction.

4. **Rewind to intake (90s).** Show the admin "add provider" flow. New provider gets magic link. Open the provider's email (mocked inbox). Provider uploads 4 documents. Agent extracts in real time; provider sees field-level cards with source citations and corrects one mis-extracted expiry date — single-field edit, the dependent validator re-runs in isolation, the rest stays cached (§7.9). Answers 3 adaptive questions. Click submit. Watch the intake state machine in Langfuse: classify → extract × 4 → validators preliminary → gap analysis → compose followup. Provider gets a single consolidated followup email asking for the 2 missing items.

5. **Round trip (60s).** Provider responds, second turn fires, agent decides the profile is complete, terminal `compute_readiness` invocation. Score appears in the admin dashboard. End on the same screen we opened on, but now with the trail of how we got there visible.

The demo opens and closes on the score because that's the differentiated product. The intake is the evidence the system works on realistic input.

---

## Appendix A — Comparison to competitors

Based on publicly marketed features as of 2026-05. Each row reflects what the competitor's marketing surface promises today, not what their engineering surface can or can't do — a deeper feature page may expose a capability the homepage doesn't promise. Sources re-verified row-by-row: Atano ([getatano.com](https://getatano.com/)), Verifiable ([verifiable.com](https://www.verifiable.com/)), Assured ([withassured.com/products/credentialing](https://www.withassured.com/products/credentialing)), Medallion ([medallion.co](https://www.medallion.co/)).

| Capability | Verifiable | Assured | Medallion | Atano (marketed) | PacketReady |
|---|---|---|---|---|---|
| Email-first pre-CAQH intake | — | — | — | — | ✓ |
| Document extraction | ✓ (CredAgent) | ✓ | partial | ✓ | ✓ |
| Cross-document validation | — | partial | — | — | ✓ (core) |
| 0–100 readiness score before submission | — | — | — | — | ✓ (core) |
| Cited audit log per decision | ✓ (CredAgent) | — | partial | — | ✓ |
| Continuous monitoring | ✓ | ✓ | ✓ | — | (follow-on) |
| Payer portal auto-fill | — | partial | — | ✓ | (out of scope) |
| Published accuracy numbers | — | — | — | — | ✓ |

Per-row reading notes (what each "partial" or "—" change reflects):

- **Verifiable.** CredAgent's marketing surfaces step-by-step decision logs with cited primary sources, and ongoing provider monitoring is its own product line. Pre-CAQH intake, cross-document validation, readiness score, and payer-portal fill are absent from the homepage — CredAgent's stated entry point is post-CAQH workflows.
- **Assured.** The credentialing page markets continuous credential-expiration tracking + OIG/Medicare/Medicaid exclusion checks as a core feature (upgraded from "partial" to ✓). "Our platform flags missing or incorrect information early" hints at cross-document validation without committing to it (downgraded from "✓" to "partial"). Auto-fill is referenced as part of "automates ... payer enrollment processes" elsewhere on the site but not promised explicitly on the credentialing page (downgraded from "✓" to "partial"). No cited-audit-log claim survives on the current page.
- **Medallion.** Real-time monitoring of credential and sanction changes is explicit. "Complete operational visibility" / "full audit visibility" appears in the delegated-credentialing copy but stops short of per-decision citations (kept as "partial"). Document extraction, cross-document validation, readiness score, and payer-portal fill are not surfaced on the homepage.
- **Atano.** "Upload any provider document and let AI extract the key information" + "Auto-fill payer applications (PDFs and portals) with existing provider data" are the two pillars surfaced. No pre-CAQH intake, no cross-document validation, no readiness score, no continuous monitoring, no cited audit log on the marketed surface.

The defensible positioning is still the column intersection: pre-CAQH intake + cross-document validation + readiness score + cited audit trail + published accuracy. No single competitor ships all five; Atano ships none of them. The "Better no claim than a wrong one" gate is the reason the rows above are conservative — every "partial" is adjacent language that doesn't quite commit to the capability, and every "—" is silence on the marketing surface, not a claim that the engineering doesn't exist.

---

## Appendix B — File layout

```
packetready/
├── README.md
├── docs/
│   ├── design.md              # this doc
│   ├── eval-results.md        # published accuracy numbers
│   └── demo-script.md
├── apps/
│   ├── api/                   # .NET, CQRS via MediatR (VaBene stack reuse)
│   ├── dashboard/             # Next.js 15, React 19, TS
│   └── intake-portal/         # Next.js 15, the magic-link upload page
├── packages/
│   ├── extractors/            # one module per doc type
│   ├── validators/            # one module per validator
│   ├── agent-runtime/         # state machine, tools, budget cap enforcement
│   ├── score/                 # synthesis logic
│   └── audit/                 # Langfuse + JSONL logging
├── prompts/
│   ├── license_extraction/
│   │   ├── v1.md
│   │   ├── v2.md
│   │   └── v3.md              # current
│   ├── identity_coherence/
│   └── ...
├── evals/
│   ├── dataset/               # 50 synthetic packets + golden labels
│   ├── runners/               # eval execution
│   └── results/               # checked-in result history
└── infra/
    ├── docker-compose.yml     # local dev
    └── neon/                  # Postgres schema, migrations
```

Stack choice (Next.js 15 / React 19 / TS for frontend, .NET 10 + MediatR + Postgres for backend, Hangfire-style background jobs, Langfuse) mirrors my VaBene stack. The reasoning is not "this is the right stack for credentialing software" — it's "this is the stack I can ship fastest with depth I can defend in an interview." A real Atano hire would adopt whatever they're already on.

---

## Appendix C — Reuse from VaBene

The "ports the architecture I designed for VaBene" claim in §1 grounds out in specific files and patterns. This appendix maps each PacketReady subsystem to the VaBene source it adapts, so reviewers can trace the provenance without taking the claim on faith.

Rough split: ~60% of the agent skeleton (runtime loop, audit, FSM, tool framework, prompt loader) ports with renames; ~40% is new construction (per-doc extractors, validator suite, score synthesis, document store with bboxes, eval harness).

### C.1 Persistence (§7.1 — document store)

| PacketReady | VaBene source | Notes |
|---|---|---|
| `documents` + `document_extractions` (append-only, JSONB) | `Domain/Entities/Agent/InquiryLog.cs`, `OnboardingSession.cs`, `OnboardingExtractionState.cs` | Lift the append-only JSONB + enum-ordinal-versioning pattern. InquiryLog uses a DB-enforced `BEFORE UPDATE` trigger for immutability — same approach for `document_extractions`. |
| Extraction-ID monotonic per document | `OnboardingExtractionState` schema-version handling | Reuse the "ordinals are append-only" rule for `schema_version`. |
| Cited reference into source PDF (`field_locations`) | *new* | No analog in VaBene; this is credentialing-specific. |

### C.2 Extraction layer (§7.2 — Haiku classify, Sonnet extract)

| PacketReady | VaBene source | Notes |
|---|---|---|
| Document classifier (Haiku, single label, confidence threshold) | `Application/Agent/Classification/InquiryClassifier.cs` | Same shape: single Haiku call, confidence < 0.7 forces ambiguity flag, no tool use. Swap inquiry intents for doc types. |
| Field-extraction tool (Sonnet, structured output, per-phase window) | `Application/Agent/Onboarding/Tools/ExtractFieldTool.cs` | Closest cousin to `extract_fields`. Reuse the current-phase + forward-window pattern and pending-fields buffering. |
| Structured-output JSON schema | `Application/Agent/Runtime/AgentResponseSchema.cs` | Per-turn JSON-Schema generation with sentinel enums for optional fields. |

### C.3 Intake state machine (§7.3)

| PacketReady | VaBene source | Notes |
|---|---|---|
| FSM (`pending → awaiting_provider → agent_processing → complete/escalated`) | `Domain/Entities/Agent/OnboardingSession.cs` + `OnboardingExtractionState.cs` | 12-phase FSM with enum-ordinal append-only versioning. Swap phases for credentialing intake. |
| Per-turn Hangfire job (load FOR UPDATE → validate → run agent → append response) | `Application/Agent/Onboarding/Jobs/OnboardingTurnJob.cs` | Lift wholesale. This is exactly the "intake agent turn-N fires" flow in §6. |
| Phase-advancement rules (required vs optional fields per phase) | `Application/Agent/Onboarding/OnboardingPhaseFieldMap.cs` | Conversational order decoupled from enum order — useful pattern for credentialing where doc-arrival order ≠ validation order. |

### C.4 Agent runtime & tools (§7.4)

| PacketReady | VaBene source | Notes |
|---|---|---|
| Multi-tool loop with three budget caps (15 steps / 80k tokens / 90s) | `Application/Agent/Runtime/InquiryAgent.cs` + `IInquiryAgent.cs` | Budgets in §7.3 are lifted verbatim from VaBene's `AgentBudget` record. |
| Per-turn mutable state (token accounting, cache-hit tracking) | `Application/Agent/Runtime/AgentLoopState.cs` | Reuse for token-cost accounting against the < $0.50/intake target. |
| Tool interface (Name, Description, JsonSchema, Boundaries, ValidateAsync, InvokeAsync) | `Application/Agent/Tools/IAgentTool.cs` | Reuse interface verbatim for the five PacketReady tools. |
| Permission-scoped tool dispatch | `Application/Agent/Tools/ToolPermissionInvoker.cs` + `ToolBoundary.cs` | Useful for "this tool can only read documents owned by this provider" — same MismatchKind taxonomy. |
| **Terminal action pattern** (`compute_readiness` ends the loop) | `Application/Agent/Onboarding/Tools/CompleteOnboardingTool.cs` | Exact pattern: terminal tool with pre-flight gates, commits to AwaitingReview, ends the loop. Renames `CompleteOnboarding` → `ComputeReadiness`. |

### C.5 Outbound / outbox (§7.5)

| PacketReady | VaBene source | Notes |
|---|---|---|
| Hold-at-send TTL (10-minute admin-yank window) | `docs/agent/subsystem-5-approval-and-send.md` (11-step atomic transaction) | Pattern is the same — "agent composes, deterministic code dispatches." VaBene's hold is 48h for capacity reasons; PacketReady's 10m is for admin review only. Lifecycle FSM is `Queued → Sent / Cancelled` — no transient `Held` state (VaBene's would-be third state earns its keep on a 48h window where operators watch in-flight rows; on 10m it doesn't, and the dispatcher's `SELECT … FOR UPDATE` row lock is what serializes concurrent dispatchers). |
| Hold-lifecycle entity (Active → terminal) | `Domain/Entities/Agent/InquiryHold.cs` | Adapt FSM states; reuse `TerminalAt` timestamp pattern for audit. |
| Two-layer idempotency (TOCTOU check + UNIQUE DB constraint) | `Application/Agent/Commands/CreateInquiryHold/CreateInquiryHoldCommandHandler.cs` | Carries over directly — protects against double-send on retry. |
| Consolidated follow-up composition (one message, all gaps) | *new* | No direct VaBene analog; `compose_followup` is a new tool that aggregates the gap list before composition. |

### C.6 Validators & score (§7.6–§7.7)

Mostly new construction. The pieces that do reuse:

| PacketReady | VaBene source | Notes |
|---|---|---|
| Cross-document LLM validator (Sonnet, structured output) | `AgentResponseSchema.cs` + Sonnet-tool dispatch in `InquiryAgent.cs` | Same JSON-schema-constrained Sonnet call shape, just with a different prompt and output schema. |
| Parallel fan-out across validators | *new* | Validators are independent — straightforward `Task.WhenAll`. No VaBene analog needed. |
| Score-synthesis weighted sum | *new* | The rubric in §7.7 is intentionally simple; no reuse. |

### C.7 Audit log (§7.8)

| PacketReady | VaBene source | Notes |
|---|---|---|
| Append-only event rows with JSONB payload, CorrelationId | `Domain/Entities/Agent/InquiryLog.cs` | Lift schema directly. EventType is a string (not an enum) for forward compatibility — keep that. |
| Dual-write API (atomic-with-transaction + independent-scope) | `Application/Agent/Runtime/IInquiryLogWriter.cs` | Reuse `AppendInTransactionAsync` for atomicity with the unit-of-work; `AppendAsync` for fire-and-forget telemetry. |
| Langfuse tag taxonomy (session.id, observation.input/output, outcome scores) | `Application/Agent/Telemetry/LangfuseTelemetry.cs` | Lift constants verbatim, rebrand `inquiry.id` → `provider.id` and `turn.id` stays. |
| Per-issue drill-in audit (citation back to extraction + tool call) | *new* | The JSONL-per-provider citation log is credentialing-specific; the underlying `AuditEvent` shape mirrors `InquiryLog`. |

### C.8 Prompt strategy (§8)

| PacketReady | VaBene source | Notes |
|---|---|---|
| Versioned prompt files loaded as embedded resources | `Application/Agent/Prompts/PromptLoader.cs` + `IPromptLoader.cs` | Reuse the embedded `.md` resource pattern, `{{var}}` substitution, ConcurrentDictionary cache. |
| Prompt resource validation at startup | `Application/Agent/Prompts/PromptResourceValidator.cs` | Reuse to catch missing/renamed prompt files at boot, not at first request. |
| **Gap: `prompt_hash` on extraction records** | *new* | VaBene relies on git for prompt versioning and does not persist a prompt hash. PacketReady's `document_extractions.prompt_hash` is non-negotiable for score-drift debugging (§7.1) — emit SHA256 at load time and write to the extraction row + audit event. |

### C.9 Docs to read before starting

| Doc | Maps to |
|---|---|
| `docs/agent/subsystem-1-persistence.md` | §7.1 (document store) |
| `docs/agent/subsystem-2-inbound-pipeline.md` | §7.2 (classifier + extraction routing) |
| `docs/agent/subsystem-3-agent-runtime.md` | §7.3–§7.4 (state machine + agent loop) |
| `docs/agent/subsystem-5-approval-and-send.md` | §7.5 (outbox + hold-at-send) |
| `docs/agent/subsystem-8-onboarding-agent.md` | End-to-end analog — closest VaBene module to PacketReady intake |

### C.10 What does *not* port

These VaBene subsystems are domain-specific to event planning and have no PacketReady analog — listed here to head off the "why not lift this too" question:

- **Pricing (subsystem-4)** — `ComputeQuoteTool`, pricing adapters, line items, fees, deposits. Event/venue-scoped.
- **Voice profile (subsystem-7)** — `MerchantVoiceProfile` capture and voice-tone matching. Tone matching for provider follow-ups is a smaller problem than merchant voice cloning; can be a system prompt, not a subsystem.
- **Venue blackout calendar, availability windows** — Event scheduling, not relevant.
- **InquiryQuote, BookedEntry capacity validators** — Booking-domain primitives.
