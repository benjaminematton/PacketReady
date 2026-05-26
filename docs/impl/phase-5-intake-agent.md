# Phase 5 — Intake Agent + Outbox

> The phase where the full lifecycle runs end-to-end. An admin enters a new hire, the provider receives a magic-link, uploads docs, and a deterministic FSM-orchestrated Sonnet agent loops to a terminal readiness score — with every outbound message gated through a 10-minute hold-at-send TTL.

| | |
|---|---|
| **Parent** | [build-plan.md](../build-plan.md) — Phase 5 row |
| **Goal** | The §6 lifecycle in [design.md](../design.md) runs end-to-end against the local stack. An admin starts an intake; the provider portal accepts uploads via a magic-link; the agent fires turn-by-turn until it invokes the terminal `compute_readiness` tool or escalates. |
| **Status** | Not started |
| **Data** | 3 demo providers in 3 tiers (green / yellow / red) seeded from `evals/fixtures/`. No production PHI. |
| **Depends on** | [Phase 3](./phase-3-extractors.md) — extractors + classifier · [Phase 4](./phase-4-scale-and-llm-validators.md) — validator suite + scoring; the agent's `compute_readiness` tool calls into `ComputeReadinessScoreCommand` as it stands at P5 start (additional validators sharpen the score later without changing the tool shape). |
| **Style** | [../style.md](../style.md) |

---

## Definition of done

- [ ] **`intake_sessions` table** + migration. One row per provider; carries the current `ProviderState` (pending / awaiting_provider / agent_processing / complete / escalated) and a JSONB `state_payload`. Single-row-per-provider invariant enforced by `UNIQUE (provider_id)`. Persisted by the FSM transition handler, never by the agent directly.
- [ ] **`outbound_messages` table** + migration. Schema matches [design.md §7.5](../design.md): `id`, `provider_id`, `turn_id`, `kind`, `subject`, `body`, `status` (`queued` / `held` / `sent` / `cancelled`), `held_until`, `composed_at`, `sent_at`. 10-minute `held_until` TTL is the abort window before dispatch.
- [ ] **`IntakeAgent` with 5 tools** wired in DI: `read_document`, `extract_fields`, `lookup_primary_source` (mocked), `compose_followup`, `compute_readiness` (terminal). Tool surface kept to exactly 5 — no `send_email`, no `update_profile`, no convenience helpers. Per-turn budgets: 15 steps · 80,000 tokens · 90s wall-clock.
- [ ] **`IntakeTurnJob`** (Hangfire) executes one agent turn per provider with a `SELECT … FOR UPDATE` row lock on `intake_sessions` so two concurrent turns can't fire for the same provider. On budget exhaustion: rollback, transition to `escalated`, emit a `partial-state` audit row.
- [ ] **Magic-link portal** (Next.js, single page) accepts uploads at `/portal/{token}`. Backed by a single-use signed token table with a 7-day expiry and a re-issue endpoint the admin can hit if the original expires. The page renders the extracted-field cards from §7.9 of the design doc — provider sees what was pulled and confirms / edits inline before submit.
- [ ] **`MockSmtpSender`** implements `IEmailSender`; writes every dispatched message to `outbox/sent/{yyyy-mm-dd}/{id}.eml` and to stdout. Real SMTP is OOS — the dispatch is what matters; the transport is a stub.
- [ ] **Per-provider turn budget cap.** A `Provider.IntakeBudgetTurns` column (default 8) is the upper bound on total agent turns. The 9th turn triggers `escalated` regardless of agent intent. Catches "agent never decides it's done" runaway.
- [ ] **`ComputeReadinessTool`** terminal action: the agent's invocation transitions the FSM out of `agent_processing` and into the score-compute pipeline. Returns `{ score, issues, computed_at }` to the agent so the final outbound message can quote the number; the actual score row is written by the score-compute handler, not the tool.
- [ ] **Audit-log spans** for every state transition + every outbound message dispatch. The dashboard's side panel walks these by `provider_id`; this is the "what did the system do for provider X" trail.
- [ ] **3 staged demo providers** end-to-end through the lifecycle on a local `docker compose up -d` stack, recorded in a 90-second loom. Green/yellow/red outcomes reproduce the readiness scores currently in `evals/fixtures/`.
- [ ] `dotnet test` + the Python runner both pass. One state-transition test per FSM edge + one outbox dedup test + one budget-exhaustion test + one tool-miss-selection test (agent calls a tool that's not in the registry — the dispatcher refuses, the agent must retry).

All ten boxes check → Phase 5 closes. Move to [Phase 6 — Demo polish](./phase-6-demo-polish.md).

---

## Stack additions

| Layer | Addition | Why |
|---|---|---|
| Backend | Hangfire.PostgreSql (already referenced in `Api.csproj` from P0) | Background-job scheduler for `IntakeTurnJob` + `OutboxDispatcherJob`. Postgres-backed so no Redis dep. |
| Backend | `Microsoft.AspNetCore.Authentication` (magic-link bearer) | Signed-token validation on portal requests. No user accounts in v1 — token IS the identity. |
| Frontend | Next.js 15 (App Router), TypeScript, Tailwind | Portal single page. No backend in Next — calls the .NET API directly. Deployed alongside the API for the demo; on its own Vercel slot later. |
| Backend | `MailKit` (interface only; mock impl ships) | `IEmailSender` abstraction. `MockSmtpSender` writes `.eml` to disk. Real SMTP slots in via a different impl when P6 demo-polish wants live email. |

No new validators, no new LLM calls in the agent runtime beyond the Sonnet calls already specced in [design.md §7.4](../design.md). The agent **reuses** the extractor + validator stack from P3/P4 verbatim — no parallel "agent runtime" layer.

---

## Decisions baked in (lock before execution)

| Decision | Choice | Why locked here |
|---|---|---|
| **FSM owns transitions, agent owns reasoning** | `IntakeSession` state transitions happen in deterministic C# code; the LLM only runs inside the `agent_turn` action body. Per [design.md §7.3](../design.md). | Mid-turn failure preserves state; the agent can't transition itself; every edge is debuggable. The orchestrator–workers pattern, not autonomous agent. |
| **Tool surface = exactly 5** | `read_document`, `extract_fields`, `lookup_primary_source`, `compose_followup`, `compute_readiness`. No `send_email`. | "Building Effective Agents" tool-inventory principle: every extra tool raises miss-selection rate. The 5 are the minimum the §6 lifecycle requires. |
| **Per-turn budget** | 15 steps · 80,000 tokens · 90s wall-clock. Identical to VaBene's `OnboardingTurnJob`. | Bounds runaway cost at ~$0.20/turn worst case. Exceeded → abort, preserve partial state, transition to `escalated`. |
| **Per-provider turn cap** | 8 turns total before forced escalation. | A 9th turn likely means the agent will never decide it's done; admin gets a "needs hands-on review" notification with the partial trace. |
| **Hold-at-send TTL** | 10 minutes. `held_until = composed_at + 10min`. The dispatcher only sends rows where `status = 'queued' AND held_until <= now()`. | Admin yank window for a misfired follow-up. Provider trust is fragile; a confused follow-up costs intake completion. Direct port from VaBene's Approval-and-Send subsystem. |
| **Outbox dedup key** | `(provider_id, turn_id, kind)` — UNIQUE. A retry of the same turn never double-sends. | Hangfire retries on transient failure; without a dedup key, a network blip duplicates the email. Identical to VaBene's outbound dedup. |
| **Magic-link token shape** | Signed JWT, 7-day expiry, single-use (consumed-at timestamp on first POST). Admin can re-issue without invalidating the underlying intake session. | 7 days matches the longest practical "I'll get to it this weekend" window without leaving the token live indefinitely. Re-issue lands in a `magic_links` table — the token is a row, not just a hash. |
| **Agent invocation = one turn per Hangfire job** | Each `IntakeTurnJob` invokes one agent loop and returns. The job does NOT spin its own retry loop. Continuation = enqueue a successor job. | Mid-turn crash recovery: the next worker pickup starts fresh against persisted state; no in-memory turn state survives a restart. |
| **`lookup_primary_source` is mocked** | The tool returns canned responses keyed by `(source, npi)` from a static lookup table. No live NPPES / OIG / SAM calls. | Real primary-source calls are P6+ work. The agent's reasoning loop is identical whether the source is real or mocked — preserves the agent test surface without external dependencies. |
| **Email transport is a file writer** | `MockSmtpSender` writes `outbox/sent/{date}/{id}.eml`. No SMTP credentials, no domain reputation. | The dispatch layer is what's load-bearing for the lifecycle. Real SMTP lands when (and only when) the demo wants to email a real Atano reviewer; until then `.eml` files in the repo are the artifact. |
| **Provider state is JSONB on the row, not normalized columns** | `state_payload JSONB` on `intake_sessions` carries the per-state discriminated-union payload (e.g. `awaiting_provider`'s `magic_link_id` + `reminder_sent_count`). | Each state shape is small and stable; normalizing means 5 nullable columns or 5 sibling tables. JSONB keeps the row count to one per provider. |
| **No real-time UX in the portal** | Single page; provider uploads, sees extraction cards, confirms or edits, submits. No web sockets, no streaming, no live agent-thought visibility. | The agent fires AFTER the provider closes the page. Streaming agent state to the provider invites confusion ("why is it still thinking?"); the followup email is the next interaction. |

---

## Project layout deltas

```
PacketReady/
├── apps/
│   ├── api/
│   │   ├── Domain/
│   │   │   ├── Intake/
│   │   │   │   ├── IntakeSession.cs              NEW — aggregate root
│   │   │   │   ├── ProviderState.cs              NEW — 5-variant discriminated union
│   │   │   │   ├── IntakeTurn.cs                 NEW — value record (turn_id, started_at, …)
│   │   │   │   └── IntakeBudget.cs               NEW — (steps, tokens, wall) caps
│   │   │   ├── Messaging/
│   │   │   │   ├── OutboundMessage.cs            NEW — aggregate
│   │   │   │   ├── OutboundMessageStatus.cs      NEW — enum
│   │   │   │   └── MessageKind.cs                NEW — enum (intake_invitation | followup | completion_notice)
│   │   │   └── MagicLinks/
│   │   │       └── MagicLink.cs                  NEW — signed-token row
│   │   ├── Application/
│   │   │   ├── Intake/
│   │   │   │   ├── Commands/
│   │   │   │   │   ├── StartIntake/              NEW
│   │   │   │   │   ├── RunAgentTurn/             NEW — MediatR command, enqueued by Hangfire
│   │   │   │   │   └── ConfirmExtraction/        NEW — provider's portal "yes this is right" action
│   │   │   │   ├── Agent/
│   │   │   │   │   ├── IIntakeAgent.cs           NEW — port; runtime impl in Infra
│   │   │   │   │   ├── ToolDefinitions.cs        NEW — the 5 tools' JSON-schema input/output shapes
│   │   │   │   │   └── AgentTurnResult.cs        NEW — record
│   │   │   │   └── Outbox/
│   │   │   │       ├── IEmailSender.cs           NEW — port; mock impl in Infra
│   │   │   │       └── ComposeFollowupHandler.cs NEW — the `compose_followup` tool's pure-code spine
│   │   │   └── Prompts/
│   │   │       └── IntakeAgentPrompt.v1.md       NEW — the system prompt
│   │   ├── Infrastructure/
│   │   │   ├── Intake/
│   │   │   │   ├── IntakeAgent.cs                NEW — Anthropic tool-use loop, budget-bounded
│   │   │   │   ├── IntakeTurnJob.cs              NEW — Hangfire wrapper; FOR UPDATE row lock
│   │   │   │   └── IntakeStateTransitioner.cs    NEW — deterministic FSM
│   │   │   ├── Outbox/
│   │   │   │   ├── OutboxDispatcherJob.cs        NEW — Hangfire; runs every 30s
│   │   │   │   ├── MockSmtpSender.cs             NEW — writes .eml files
│   │   │   │   └── OutboxDedupIndex.cs           NEW — (provider_id, turn_id, kind) UNIQUE
│   │   │   ├── MagicLinks/
│   │   │   │   ├── MagicLinkIssuer.cs            NEW — JWT signer + DB row
│   │   │   │   └── MagicLinkAuthHandler.cs       NEW — ASP.NET bearer scheme
│   │   │   ├── PrimarySources/
│   │   │   │   └── MockPrimarySourceLookup.cs    NEW — keyed by (source, npi)
│   │   │   └── Persistence/
│   │   │       └── Migrations/
│   │   │           └── 202606xxxxxxxx_AddIntake.cs  NEW — intake_sessions, outbound_messages, magic_links
│   │   ├── Api/
│   │   │   ├── Endpoints/Intake/
│   │   │   │   ├── StartIntakeEndpoint.cs        NEW — POST /api/intakes (admin)
│   │   │   │   ├── PortalSubmitEndpoint.cs       NEW — POST /portal/{token}/submit (magic-link auth)
│   │   │   │   └── PortalGetEndpoint.cs          NEW — GET  /portal/{token} (extraction cards)
│   │   │   └── Hangfire/
│   │   │       └── HangfireSetup.cs              NEW — recurring outbox dispatcher + on-demand turn jobs
│   │   └── Tests/
│   │       ├── Domain/Intake/                    NEW — FSM edge tests
│   │       ├── Application/Intake/                NEW — agent-turn handler tests, mocked IIntakeAgent
│   │       ├── Infrastructure/Outbox/            NEW — dedup, TTL, mock-SMTP tests
│   │       └── Integration/                       NEW — full lifecycle on test DB
├── portal/                                        NEW — Next.js 15 single-page app
│   ├── app/
│   │   ├── [token]/page.tsx                       NEW — extraction-card UI + upload + submit
│   │   └── api/                                   absent — talks to .NET API directly
│   ├── lib/api.ts                                 NEW — typed client for /portal/{token} endpoints
│   ├── package.json
│   └── tailwind.config.ts
└── docker-compose.yml                            EXTEND — add Hangfire dashboard binding (port 5050)
```

---

## File-by-file

### `apps/api/Domain/Intake/IntakeSession.cs`

Aggregate root. One row per provider. Holds the current `ProviderState` and the audit-trail bookkeeping (turn count, last-transition timestamp). Every state transition is a method on this aggregate; the FSM rules live here, not in the handler.

```csharp
public sealed class IntakeSession
{
    public Guid Id { get; private set; }
    public Guid ProviderId { get; private set; }
    public ProviderState State { get; private set; }
    public int TurnsConsumed { get; private set; }
    public int TurnBudget { get; private set; }  // default 8 per Provider.IntakeBudgetTurns
    public DateTimeOffset LastTransitionAt { get; private set; }

    public static IntakeSession Start(Guid providerId, int turnBudget, DateTimeOffset nowUtc) { … }
    public void BeginAgentTurn(Guid turnId, DateTimeOffset nowUtc) { … }
    public void EndAgentTurn(AgentTurnResult result, DateTimeOffset nowUtc) { … }
    public void Escalate(string reason, DateTimeOffset nowUtc) { … }
    public void Complete(Guid readinessScoreId, DateTimeOffset nowUtc) { … }
}
```

Throwing on illegal transitions (e.g. `BeginAgentTurn` from `complete`) is the safety net the integration tests rely on.

### `apps/api/Domain/Intake/ProviderState.cs`

Discriminated-union types from [design.md §7.3](../design.md). C# 12 `abstract sealed record` hierarchy:

```csharp
public abstract record ProviderState
{
    public sealed record Pending(DateTimeOffset CreatedAt) : ProviderState;
    public sealed record AwaitingProvider(Guid MagicLinkId, int RemindersSent) : ProviderState;
    public sealed record AgentProcessing(Guid TurnId, DateTimeOffset StartedAt) : ProviderState;
    public sealed record Complete(Guid ReadinessScoreId, DateTimeOffset CompletedAt) : ProviderState;
    public sealed record Escalated(string Reason, string PartialProfileJson) : ProviderState;
}
```

Serialized to `intake_sessions.state_payload` JSONB via STJ + `JsonStringEnumConverter`. STJ's `[JsonDerivedType]` polymorphism keeps the round-trip clean — no manual switch dispatch in the persistence layer.

### `apps/api/Infrastructure/Intake/IntakeAgent.cs`

The tool-use loop. Single `RunTurnAsync(providerId, turnId, ct)` entrypoint. Loads the per-provider context (current state, document list, prior turn artifacts), opens an Anthropic Messages stream with `tool_choice = auto`, dispatches tool calls against the registered handlers, accumulates the response into an `AgentTurnResult`.

Budget enforcement is in the loop itself:

```csharp
while (!response.StopReason.IsTerminal())
{
    if (stepsConsumed >= MaxSteps) throw new BudgetExhaustedException("steps");
    if (tokensConsumed >= MaxTokens) throw new BudgetExhaustedException("tokens");
    if (clock.Elapsed >= MaxWallClock) throw new BudgetExhaustedException("wall");
    // … dispatch tool, accumulate, re-prompt
}
```

`BudgetExhaustedException` is caught by `IntakeTurnJob`, transitions the session to `escalated` with the budget-axis as the reason. Other exceptions surface as a Hangfire retry.

The 5 tools are registered in DI as `IIntakeTool[]`. Each implements:

```csharp
public interface IIntakeTool
{
    string Name { get; }
    JsonElement InputSchema { get; }
    Task<JsonElement> InvokeAsync(JsonElement args, Guid providerId, Guid turnId, CancellationToken ct);
    bool IsTerminal => false;  // true only for ComputeReadinessTool
}
```

Dispatcher refuses an unknown tool name; the agent gets back an error message in the next turn loop iteration. This is the "miss-selection" mode the [design.md §7.4](../design.md) tool-inventory rule guards against — the agent occasionally invents a `update_profile` tool that doesn't exist; the dispatcher must say no without crashing.

### `apps/api/Application/Intake/Agent/ToolDefinitions.cs`

The 5 tools' JSON-schema input/output shapes, one per tool. Anthropic's tool-use surface expects a schema per tool call; these are loaded once at startup and reused across every agent invocation.

Per [design.md §7.4](../design.md):

| Tool | Input | Output |
|---|---|---|
| `read_document` | `{ document_id }` | `{ doc_type, extracted_fields, field_locations }` |
| `extract_fields` | `{ document_id, schema }` | `{ fields, field_locations, confidence }` |
| `lookup_primary_source` | `{ source, identifiers }` | `{ found, fields, mismatch_fields }` |
| `compose_followup` | `{ provider_id, gaps }` | `{ subject, body }` |
| `compute_readiness` | `{ provider_id }` | `{ score, issues, computed_at }` — TERMINAL |

`compute_readiness` invokes `ComputeReadinessScoreCommand` (P3/P4 surface) verbatim. No parallel score path; the agent's terminal action goes through the same handler as the admin's manual score request.

### `apps/api/Infrastructure/Intake/IntakeTurnJob.cs`

Hangfire job. Single method body, called by the orchestrator when an agent turn is due.

```csharp
public async Task RunAsync(Guid providerId, CancellationToken ct)
{
    await using var tx = await _db.Database.BeginTransactionAsync(ct);

    // Row lock so two concurrent turn jobs on the same provider serialize.
    var session = await _db.IntakeSessions
        .FromSqlInterpolated($"SELECT * FROM intake_sessions WHERE provider_id = {providerId} FOR UPDATE")
        .SingleAsync(ct);

    if (session.TurnsConsumed >= session.TurnBudget)
    {
        session.Escalate("turn-budget-exhausted", _clock.GetUtcNow());
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return;
    }

    var turnId = Guid.NewGuid();
    session.BeginAgentTurn(turnId, _clock.GetUtcNow());
    await _db.SaveChangesAsync(ct);

    try
    {
        var result = await _agent.RunTurnAsync(providerId, turnId, ct);
        session.EndAgentTurn(result, _clock.GetUtcNow());
        if (result.OutboundProposed is not null) _outbox.Enqueue(result.OutboundProposed);
    }
    catch (BudgetExhaustedException ex)
    {
        session.Escalate($"budget:{ex.Axis}", _clock.GetUtcNow());
    }

    await _db.SaveChangesAsync(ct);
    await tx.CommitAsync(ct);
}
```

The `FOR UPDATE` lock + the explicit transaction is what makes "two concurrent turns for the same provider" impossible. Hangfire's at-least-once delivery is fine because the lock serializes; the second invocation just sees the post-state and short-circuits.

### `apps/api/Infrastructure/Outbox/OutboxDispatcherJob.cs`

Recurring Hangfire job, fires every 30 seconds. Pulls rows where `status = 'queued' AND held_until <= now()`, dispatches them through `IEmailSender`, updates `status = 'sent', sent_at = now()`.

The 10-minute TTL is in the *select* clause — the dispatcher physically cannot send a row before its `held_until`. Admin yank = `UPDATE outbound_messages SET status = 'cancelled' WHERE id = ?` before the TTL elapses.

Dedup: the `(provider_id, turn_id, kind)` UNIQUE index catches a re-enqueue of the same logical message. A second `INSERT` from a retried `IntakeTurnJob` raises `unique_violation`; the outbox handler swallows it and logs a "dedup hit" — same pattern as `ExtractionPersister` from P3.

### `portal/app/[token]/page.tsx`

Single Next.js page. On GET, calls the .NET API's `GET /portal/{token}` which returns:

```typescript
type PortalState = {
  provider: { fullName: string; specialty: string };
  documents: Array<{
    id: string;
    docType: string;
    fields: Record<string, { value: string; confidence: number; page: number; bbox: [number, number, number, number] }>;
  }>;
  pendingUploads: string[];  // requested doc types not yet uploaded
};
```

Renders one card per document with the extracted fields visible + inline-editable (per [design.md §7.9](../design.md)). Below: drop zone for the pending uploads. Submit → `POST /portal/{token}/submit` with the confirmed/edited field map.

No state library; React Server Components for the GET, a server action for the submit. Smallest possible page; the visual chrome lands in P6 polish.

### `apps/api/Application/Prompts/IntakeAgentPrompt.v1.md`

System prompt for the tool-use loop. Sets the agent's role, the tool catalog, the FSM context, and the budget rules. Pins to ~1,500 input tokens; each turn pays this once + the per-turn context + the tool-result trips.

The prompt's hash lands on `intake_turns.prompt_hash` so a prompt edit re-extracts the same provider's turns idempotently against the v2 prompt (matching the extractor idempotency contract from P3).

---

## Task order

1. **Migration: `intake_sessions` + `outbound_messages` + `magic_links` tables.** `dotnet ef migrations add AddIntake --output-dir Persistence/Migrations`. The migration runs against a clean DB; existing P3/P4 `providers` rows back-populate with `intake_sessions.state = 'complete'` so the new table doesn't break existing readiness queries.
2. **Domain types: `IntakeSession` aggregate + `ProviderState` union.** Edge-case-driven tests on the FSM (every illegal transition throws; every legal transition updates `LastTransitionAt`). No DB, no LLM.
3. **`OutboundMessage` aggregate + `MockSmtpSender`.** Pure-code; the dispatcher's filter logic is testable against an in-memory DB. Hold-at-send TTL test pins the < `held_until` rejection.
4. **`StartIntakeCommand` + admin endpoint `POST /api/intakes`.** Creates a `Provider` + `IntakeSession(pending)` + a `MagicLink` row + an `intake_invitation` outbox row in one transaction.
5. **`MagicLinkIssuer` + ASP.NET bearer scheme.** Token = signed JWT with the link id; scheme calls into the issuer to validate single-use + expiry. Test: an expired token returns 410; a consumed token returns 410; a fresh token returns 200.
6. **Portal `GET /portal/{token}` + `POST /portal/{token}/submit`.** The Next.js page can hit a stub for the first cut; the .NET endpoints come first because the portal renders against them.
7. **Tool registry + 5 tool implementations.** Each tool is a pure-code class (the LLM doesn't see implementations — the agent calls them via JSON-schema'd surfaces). `lookup_primary_source` ships with a 5-entry mock table covering the 3 demo providers + 2 edge cases. `compute_readiness` is a thin wrapper over `ComputeReadinessScoreCommand`.
8. **`IntakeAgent.RunTurnAsync` — the tool-use loop.** Anthropic.SDK's `Messages` API with `tool_choice = auto`. Budget enforcement inline. Result accumulation. Unit-test with a recorded tool-call sequence (the agent harness for VaBene has this pattern; port it).
9. **`IntakeTurnJob` with `FOR UPDATE` row lock + Hangfire scheduling.** The recurring-trigger logic is: when a provider's portal submit lands, enqueue one turn job; when the turn job finishes and the agent proposed a follow-up, the outbox dispatcher's own job picks up the new row. No cron-style polling.
10. **`OutboxDispatcherJob` recurring every 30s.** Hangfire recurring registration in `HangfireSetup`. Per-row try/catch so one bad row doesn't stall the queue.
11. **Portal Next.js page (single file).** App Router; server action for submit; Tailwind chrome. No client-state library.
12. **3 demo providers staged end-to-end.** Run the local stack, walk the lifecycle for each of the three tiers, capture the loom.
13. **Integration tests on the full lifecycle.** One test per tier (green/yellow/red) that calls `POST /api/intakes`, simulates the portal POST with the right doc set, asserts the FSM lands in `complete` with the expected score range.
14. **Update [build-plan.md](../build-plan.md) Status: Phase 5 → closed.** Then write `phase-6-demo-polish.md`.

Order matters: 1 unblocks 2+3+4; 4 unblocks 5+6; 7 unblocks 8; 8+10 unblock 9; 9+11 unblock 12+13.

---

## Risks / open

- **Agent never invokes `compute_readiness`.** The terminal-tool pattern relies on the agent recognizing "we have enough." If the prompt is too permissive about asking-for-more, the budget cap fires and every provider lands in `escalated`. Mitigation: the prompt names `compute_readiness` explicitly as the way to end the loop and shows one example trace where it's invoked with two missing minor fields ("close enough to score; admin can fill the rest in dashboard"). If escalation rate runs hot (> 20%) on the 3 demo providers, the prompt needs retuning before P5 closes.
- **Tool-call miss-selection.** Anthropic's tool-use is well-behaved on Sonnet but occasionally invents a tool name. The dispatcher's refusal returns a structured error back into the agent's context, but two consecutive misses in one turn waste budget. Watch the turn logs in early demo runs; if misses are > 1/turn, narrow the prompt's tool descriptions.
- **Concurrent turns for the same provider.** The `FOR UPDATE` lock + the `UNIQUE (provider_id)` on `intake_sessions` are belt-and-braces. The failure mode would be two Hangfire workers picking up the same enqueued job from a misconfigured queue; the second one blocks on the lock, then sees the new state and short-circuits. Tested via an explicit two-worker integration test.
- **Outbox runaway after a retry storm.** Hangfire retries `IntakeTurnJob` on transient failure; each retry re-runs the turn, which re-proposes a follow-up. The `(provider_id, turn_id, kind)` UNIQUE catches the dup but a malformed dedup key wouldn't. Pin the index by name (`ux_outbound_messages_dedup`) and let `IsUniqueViolation` check the constraint name, same pattern as `ExtractionPersister`.
- **Magic-link replay.** Single-use enforcement requires a transaction at consume time: SELECT FOR UPDATE the `magic_links` row, check `consumed_at IS NULL`, set `consumed_at = now()`, commit. Without the lock, two concurrent clicks could both succeed; with it, the second gets a 410. Test pins this race.
- **Portal renders against an unconfirmed extraction.** If the portal renders extraction cards before the agent's first turn confirms the doc-type classifier output, the user sees a card labeled "license" for what's actually a board cert. Mitigation: the portal `GET` only returns documents whose classifier confidence is ≥ 0.85 OR which the agent has touched. Mid-band classifications get a "we're still reviewing this doc" placeholder.
- **JWT signing-key rotation in dev.** The local stack uses a static signing key from `.env`. In CI / demo, a key change invalidates outstanding magic links. Acceptable for the demo loop; the docs note "rotating the signing key clears the magic-link queue."
- **3 demo providers is a small sample.** The end-to-end test passes on 3; the 50-packet eval set isn't an end-to-end test (it's an extraction-and-scoring test, no agent). If the agent's behavior on packet-#27 differs from the staged 3, we won't catch it in P5. Mitigation: a P6 follow-on sweeps a random 10 of the 50 packets through the lifecycle as a regression.

---

## Out of scope (resist)

- **Real CAQH ProView integration.** Mocked throughout the agent's `lookup_primary_source`. Real CAQH lives in a post-launch phase that needs partner credentials.
- **Real SMTP.** `MockSmtpSender` is the v1 transport. Real SMTP (SendGrid / Postmark / SES) lands when the demo wants to email a real reviewer.
- **Browser-driven payer portal auto-fill.** The end of the lifecycle is "admin gets a notified dashboard"; submission to a payer is human-driven.
- **Provider account creation / multi-session login.** Magic link IS the identity. A provider clicks the link, uploads, closes the tab. No "save progress and come back later" beyond the link's 7-day expiry.
- **Multi-tenancy.** Single-tenant: this PacketReady deployment scores one credentialing org's providers. Multi-tenant adds row-level security + tenant headers + a `tenants` table — all post-launch.
- **Real-time agent UI.** No web sockets to the portal. The next interaction with the provider is an email, not a live status page.
- **Score recompute on prompt change.** When a P4 validator's prompt bumps, existing readiness scores don't auto-refresh. Future re-scoring is a manual admin action ("re-score provider X against latest prompts"); P5 ships without it.
- **Audit-log UI.** The audit rows are written; the UI to browse them is the P6 dashboard's side panel. P5 verifies via SQL.

---

## What gets written when Phase 5 closes

Append a one-line outcome note to [build-plan.md](../build-plan.md) Status. Then write `phase-6-demo-polish.md`. Topics: dashboard drill-in (PDF preview + bbox highlighting + audit side panel + Langfuse deep-link), 3 pre-staged green/yellow/red demo providers, 5-minute demo script with 3× rehearsal, README hero section with the comparison table from [design.md Appendix A](../design.md) + a 60-second Loom of the lifecycle.

The hand-off point for Atano outreach is here — after P5 closes, the lifecycle runs end-to-end and the demo is genuinely usable. P6 sharpens; it doesn't ship the central artifact.
