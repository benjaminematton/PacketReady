# Phase 0 — Walking Skeleton

> Prove the boring infrastructure works before any product code lands on top of it.

| | |
|---|---|
| **Parent** | [build-plan.md](../build-plan.md) — Phase 0 row |
| **Goal** | One HTTP request hits Claude, writes an audit row, emits a Langfuse span, returns a typed response. |
| **Status** | Closed 2026-05-21 — 3 of 4 gates green; Langfuse trace rendering deferred (see "Closing notes") |
| **Companion** | [design.md](../design.md) §7.8 (audit log), Appendix C.7 |

---

## Definition of done

A `POST /api/ping` with body `{ "message": "hello" }` returns:

```json
{
  "reply": "Hello! How can I help you today?",
  "model": "claude-haiku-4-5",
  "audit_event_id": "0b8c4a2a-…",
  "langfuse_trace_id": "trace_…",
  "input_tokens": 12,
  "output_tokens": 9,
  "cost_usd": 0.000045
}
```

Verifiable by hand:

- [ ] Row in `audit_events` with `event_type = 'PingExecuted'`, payload contains the request body + token usage.
- [ ] Trace visible at `http://localhost:3000` (Langfuse) with one span named `ping.invoke`, model + cost rendered.
- [ ] Killing Postgres mid-request → API returns 500, no Langfuse span (writes are atomic — audit row write is in the same transaction as the response prep). *Loosen if friction; the strict version is the right north star.*
- [ ] Killing Langfuse mid-request → API returns 200 anyway (telemetry is fire-and-forget). Audit row still written.

If all four boxes check, Phase 0 is done. Move to [Phase 1](./phase-1-score-from-clean-input.md) — write that doc next.

---

## Stack lockdown

Inherited from VaBene without modification unless a strong reason emerges.

| Layer | Choice | Version |
|---|---|---|
| Runtime | .NET | `net10.0` |
| HTTP | ASP.NET Core minimal API | bundled |
| ORM | EF Core + Npgsql | `10.0.4` / `10.0.1` |
| DB | Postgres | 16 (docker) |
| LLM | Anthropic.SDK | `5.10.0` |
| Background jobs | Hangfire + Hangfire.PostgreSql | `1.8.23` / `1.20.13` |
| CQRS | MediatR | latest |
| Observability | OpenTelemetry + Langfuse OTel ingest | `1.15.x` |
| Frontend | *not in Phase 0* | — |

Anthropic.SDK 5.10.0 is what VaBene's `InquiryAgent.cs` builds against — keeps the port path clean. **Do not** swap for a raw HttpClient just because the SDK API is awkward; the port relies on the SDK's tool-call types.

---

## Project layout

```
PacketReady/
├── docker-compose.yml
├── .env.example
├── apps/
│   └── api/
│       ├── PacketReady.sln
│       ├── Domain/
│       │   ├── Domain.csproj
│       │   └── Audit/
│       │       ├── AuditEvent.cs
│       │       └── AuditEventType.cs
│       ├── Application/
│       │   ├── Application.csproj
│       │   ├── Audit/
│       │   │   ├── IAuditWriter.cs
│       │   │   └── Commands/AppendAuditEvent/
│       │   ├── Prompts/
│       │   │   ├── IPromptLoader.cs
│       │   │   ├── PromptLoader.cs
│       │   │   └── PromptResourceValidator.cs
│       │   └── Ping/
│       │       └── PingCommand.cs
│       ├── Infrastructure/
│       │   ├── Infrastructure.csproj
│       │   ├── Persistence/
│       │   │   ├── PacketReadyDbContext.cs
│       │   │   ├── Configurations/AuditEventConfiguration.cs
│       │   │   └── Migrations/   (generated)
│       │   ├── Audit/
│       │   │   └── AuditWriter.cs
│       │   ├── Anthropic/
│       │   │   └── AnthropicClientFactory.cs
│       │   └── Telemetry/
│       │       └── LangfuseTelemetry.cs
│       └── Api/
│           ├── Api.csproj
│           ├── Program.cs
│           └── Endpoints/PingEndpoint.cs
└── docs/
    └── impl/phase-0-walking-skeleton.md  (this doc)
```

**Why split projects in Phase 0:** the project-reference graph (`Api → Application → Domain`, `Infrastructure → Application, Domain`) is a structural test that catches accidental dependencies (Domain referencing EF Core, Application referencing HTTP types). Cheap to set up now, painful to retrofit.

**Reference graph:**

```
Api → Application, Infrastructure
Infrastructure → Application, Domain
Application → Domain
Domain → (nothing)
```

---

## File-by-file

### Domain

**`Domain/Audit/AuditEvent.cs`** — port of VaBene `InquiryLog`, stripped of `InquiryId`/`MerchantId`, with `ProviderId` + `TurnId` instead.

```csharp
namespace PacketReady.Domain.Audit;

/// <summary>
/// Append-only event row for every action across every provider intake.
/// DB-enforced via BEFORE UPDATE/DELETE trigger.
/// </summary>
public class AuditEvent
{
    public Guid Id { get; private set; }
    public Guid? ProviderId { get; private set; }   // nullable for pre-provider events (e.g. ping)
    public Guid? TurnId { get; private set; }
    public string EventType { get; private set; } = null!;  // plain TEXT, see AuditEventType
    public string Payload { get; private set; } = "{}";     // JSONB
    public DateTimeOffset OccurredAt { get; private set; }
    public Guid? CorrelationId { get; private set; }

    private AuditEvent() { }

    public static AuditEvent Create(
        string eventType,
        string payloadJson,
        Guid? providerId = null,
        Guid? turnId = null,
        Guid? correlationId = null,
        DateTimeOffset? occurredAt = null)
    {
        if (string.IsNullOrWhiteSpace(eventType))
            throw new ArgumentException("Event type is required.", nameof(eventType));

        return new AuditEvent
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            Payload = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
            ProviderId = providerId,
            TurnId = turnId,
            CorrelationId = correlationId,
            OccurredAt = occurredAt ?? DateTimeOffset.UtcNow,
        };
    }
}
```

**`Domain/Audit/AuditEventType.cs`** — start tiny. Phase 0 only needs `PingExecuted`; future phases will add their event types here as they land. EventType is `TEXT`, not enum — same forward-compat reasoning as VaBene.

```csharp
namespace PacketReady.Domain.Audit;

public static class AuditEventType
{
    public const string PingExecuted = "PingExecuted";
    // Phase 1+ add their own.
}
```

### Application

**`Application/Audit/IAuditWriter.cs`** — dual-write API (atomic + fire-and-forget), ported from VaBene `IInquiryLogWriter`.

```csharp
namespace PacketReady.Application.Audit;

public interface IAuditWriter
{
    /// <summary>Writes in the caller's unit-of-work. Use when audit + business write must be atomic.</summary>
    Task<Guid> AppendInTransactionAsync(AuditEvent evt, CancellationToken ct);

    /// <summary>Writes in an independent scope. Use for fire-and-forget telemetry.</summary>
    Task<Guid> AppendAsync(AuditEvent evt, CancellationToken ct);
}
```

**`Application/Ping/PingCommand.cs`** — the one MediatR command Phase 0 introduces. Validates the pattern.

```csharp
public record PingCommand(string Message) : IRequest<PingResult>;

public record PingResult(
    string Reply,
    string Model,
    Guid AuditEventId,
    string LangfuseTraceId,
    int InputTokens,
    int OutputTokens,
    decimal CostUsd);
```

Handler invokes Anthropic.SDK (Haiku, single-turn), wraps the call in a Langfuse span, writes one `AuditEvent` with payload `{ request, response, usage }`. Audit write happens in-transaction; Langfuse span is fire-and-forget.

**`Application/Prompts/*`** — copy verbatim from VaBene:
- `backend/Application/Agent/Prompts/IPromptLoader.cs`
- `backend/Application/Agent/Prompts/PromptLoader.cs`
- `backend/Application/Agent/Prompts/PromptResourceValidator.cs`

Rename namespace `VaBene.Application.Agent.Prompts` → `PacketReady.Application.Prompts`. No embedded prompts yet — just the loader infrastructure. PromptResourceValidator will run at startup against an empty manifest and pass.

### Infrastructure

**`Infrastructure/Persistence/PacketReadyDbContext.cs`** — single `DbSet<AuditEvent> AuditEvents` for now.

**`Infrastructure/Persistence/Configurations/AuditEventConfiguration.cs`** — Fluent API:
- Table `audit_events`.
- `Payload` mapped as `jsonb`.
- Indexes: `(ProviderId, OccurredAt)`, `(CorrelationId)`, `(OccurredAt)` for time-range scans.
- No FK to providers — `ProviderId` is a soft reference (providers table doesn't exist until Phase 1).

**Migration SQL (additive after `dotnet ef migrations add Init`):**

```sql
-- Append-only enforcement: raise unless GUC is set.
CREATE OR REPLACE FUNCTION audit_events_block_update_delete()
RETURNS TRIGGER AS $$
BEGIN
  IF current_setting('app.allow_audit_scrub', true) = 'on' THEN
    RETURN COALESCE(NEW, OLD);
  END IF;
  RAISE EXCEPTION 'audit_events is append-only (% on %)', TG_OP, OLD.id;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER audit_events_immutable
BEFORE UPDATE OR DELETE ON audit_events
FOR EACH ROW EXECUTE FUNCTION audit_events_block_update_delete();
```

Apply the trigger by writing a custom migration `Up()` method after the auto-generated table — same approach VaBene uses for InquiryLog.

**`Infrastructure/Audit/AuditWriter.cs`** — implements `IAuditWriter`. `AppendInTransactionAsync` calls `_db.AuditEvents.Add` + does NOT call `SaveChangesAsync` (caller's responsibility). `AppendAsync` opens an `IDbContextFactory<PacketReadyDbContext>` scope and saves immediately.

**`Infrastructure/Anthropic/AnthropicClientFactory.cs`** — single `AnthropicClient` registered as singleton, reads `ANTHROPIC_API_KEY` from `IConfiguration`. Fail fast at startup if missing.

**`Infrastructure/Telemetry/LangfuseTelemetry.cs`** — port from VaBene verbatim, rename `inquiry.id` → `provider.id`, keep `turn.id`. Adds OTel `ActivitySource` named `PacketReady` and the score-name constants.

### Api

**`Api/Program.cs`** — minimal-API host. Registers:

```csharp
builder.Services
    .AddDbContextPool<PacketReadyDbContext>(o => o.UseNpgsql(connStr))
    .AddDbContextFactory<PacketReadyDbContext>(o => o.UseNpgsql(connStr))
    .AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(PingCommand).Assembly))
    .AddScoped<IAuditWriter, AuditWriter>()
    .AddSingleton<IPromptLoader, PromptLoader>()
    .AddSingleton(sp => new AnthropicClient(sp.GetRequiredService<IConfiguration>()["ANTHROPIC_API_KEY"]!))
    .AddOpenTelemetry()
        .WithTracing(t => t
            .AddSource("PacketReady")
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(o => o.Endpoint = new Uri(builder.Configuration["LANGFUSE_OTEL_ENDPOINT"]!)));
```

PromptResourceValidator runs in `app.Lifetime.ApplicationStarted` and fails-fast on missing prompts.

**`Api/Endpoints/PingEndpoint.cs`** — minimal-API endpoint:

```csharp
app.MapPost("/api/ping", async (PingRequest req, IMediator mediator, CancellationToken ct) =>
{
    var result = await mediator.Send(new PingCommand(req.Message), ct);
    return Results.Ok(result);
});
```

---

## docker-compose.yml

```yaml
services:
  postgres:
    image: postgres:16
    environment:
      POSTGRES_USER: packetready
      POSTGRES_PASSWORD: packetready
      POSTGRES_DB: packetready
    ports: ["5432:5432"]
    volumes: [pgdata:/var/lib/postgresql/data]

  langfuse-db:
    image: postgres:16
    environment:
      POSTGRES_USER: langfuse
      POSTGRES_PASSWORD: langfuse
      POSTGRES_DB: langfuse
    volumes: [langfuse_pgdata:/var/lib/postgresql/data]

  langfuse:
    image: langfuse/langfuse:latest
    depends_on: [langfuse-db]
    environment:
      DATABASE_URL: postgresql://langfuse:langfuse@langfuse-db:5432/langfuse
      NEXTAUTH_SECRET: dev-only-not-secret
      NEXTAUTH_URL: http://localhost:3000
      SALT: dev-only-not-secret
    ports: ["3000:3000"]

volumes:
  pgdata:
  langfuse_pgdata:
```

Bring up: `docker compose up -d`. Langfuse first run: create account at `http://localhost:3000`, generate a project, copy public + secret keys into `.env`.

---

## .env.example

```
ANTHROPIC_API_KEY=sk-ant-...
DB_CONNECTION_STRING=Host=localhost;Port=5432;Database=packetready;Username=packetready;Password=packetready
LANGFUSE_PUBLIC_KEY=pk-lf-...
LANGFUSE_SECRET_KEY=sk-lf-...
LANGFUSE_OTEL_ENDPOINT=http://localhost:3000/api/public/otel
```

Real `.env` is gitignored.

---

## Task order

Build in this order. Each task should compile + pass any tests before the next starts.

1. **Solution + project skeleton.** Create the 4 projects + reference graph + `.sln`. `dotnet build` from `apps/api/` passes with empty projects.
2. **Postgres + Langfuse via docker-compose.** `docker compose up -d` succeeds; can connect to Postgres with `psql`; Langfuse UI loads.
3. **EF Core + first migration.** `AuditEvent` entity + configuration + `Init` migration. `dotnet ef database update` creates the table.
4. **Trigger migration.** Hand-write `Up()` with the trigger SQL. Apply. Verify by attempting `UPDATE audit_events SET payload = '{}' WHERE id = …` and seeing the trigger raise.
5. **IAuditWriter + AuditWriter.** Unit test: AppendAsync writes one row visible after the call returns.
6. **Anthropic.SDK wiring.** Smoke test (xUnit, hits real API): one `Messages.CreateAsync` call to Haiku returns content. Skip on CI / no API key.
7. **Langfuse OTel wiring.** Force one span emission from `Program.cs`. Verify in UI.
8. **PromptLoader port.** Copy the 3 files, fix namespaces, register in DI. Startup validator passes (empty manifest).
9. **PingCommand + handler.** Wire MediatR. Handler does: open OTel span → call Haiku → write AuditEvent → return PingResult.
10. **`/api/ping` endpoint.** Wire to MediatR. End-to-end smoke test.
11. **Gate verification.** Walk the four checkboxes at the top.

Order matters: 3 → 4 (trigger needs the table); 7 depends on 2 (Langfuse must be reachable); 9 depends on 5 + 6 + 7.

---

## Risks / open

- **Langfuse OTel ingest endpoint shape** may change between Langfuse versions. If span doesn't render in UI, check Langfuse docs for current OTel path; the `LANGFUSE_OTEL_ENDPOINT` env var isolates the choice.
- **Anthropic.SDK 5.10.0 + .NET 10 RTM compatibility** — confirm at first build. If broken, pin a different SDK version *before* writing handler code against it.
- **Append-only trigger + EF migrations interaction** — EF treats triggers as foreign state. Writing the trigger in a custom migration `Up()` is the cleanest path; do **not** rely on EF generating it.
- **Hangfire is registered but unused in Phase 0.** Set up infrastructure (schema migrations, dashboard mount) but don't enqueue any jobs yet. Phase 5 needs it, P0 just confirms it doesn't fight the DI container.
- **Decision pending:** scope of `audit_events.ProviderId` foreign key. Defer to Phase 1 when `providers` table exists — until then, soft reference is fine.

---

## Out of scope (resist)

- Auth on `/api/ping`. It's a smoke endpoint; localhost only.
- Per-environment configuration. `appsettings.json` + env vars only.
- Structured logging beyond OTel. Console logger is fine.
- Health-check endpoint, readiness probe, k8s manifests.
- CI pipeline. Phase 6 problem.
- The frontend. Not until Phase 1.

---

## Closing notes (2026-05-21)

**What landed:** every code-side P0 file. Build green, 16 unit tests pass, schema applied with append-only trigger verified. `POST /api/ping` returns a typed `PingResult` with reply, model, audit_event_id, trace_id, token counts, cost. Audit row visible in `audit_events` with the same id the response surfaced. Postgres-kill returns 500; Langfuse-unreachable returns 200 (telemetry truly fire-and-forget per the `AppendAsync` swallow).

**Package pin learned in P0:** `Microsoft.Extensions.AI` must be `10.3.0`, not the default-latest. `Anthropic.SDK 5.10.0`'s `ChatClientHelper` references `HostedMcpServerTool.AuthorizationToken` which was removed in `Microsoft.Extensions.AI 10.4+`. Both projects (Application, Infrastructure) pin 10.3.0 explicitly.

**Deferred: Langfuse trace rendering.** Langfuse v2.95 (the `langfuse/langfuse:2` image) does not expose an OTLP receiver at `/api/public/otel/v1/traces` — that endpoint is a v3 feature. The OTel `ActivitySource` is firing correctly in-process (the `PingResult.TraceId` field is a real 16-byte trace id) and the OTLP exporter is wired; only the receiver is missing.

Two paths forward, neither blocking on Phase 1:

1. **Upgrade to Langfuse v3.** Adds Clickhouse + Redis + langfuse-worker + minio to docker-compose (5–6 services total). The receiver path becomes `/api/public/otel/v1/traces` (same as the wired client). Best long-term.
2. **Add Jaeger or Tempo for traces.** Single-container OTLP receiver, swap Langfuse for trace-only viewing. Diverges from design doc's "Langfuse" naming; reversible.

Pick when Phase 4 evals need trace search — until then the audit log + Langfuse REST scores API (Phase 4) covers the observability surface that's load-bearing for the product.

**Bring-up gotcha:** `.env` connection-string values containing `;` must be quoted (`DB_CONNECTION_STRING="Host=...;Port=..."`) or shell sourcing chops them at the first semicolon. Updated `.env.example` accordingly.

**Bring-up convenience:** `ANTHROPIC_API_KEY` lives in .NET user-secrets (UserSecretsId `076bff29-bc91-4480-8c5b-4480507f0cd3`), not `.env`. Auto-loaded in Development environment.

## Next

Write [phase-1-score-from-clean-input.md](./phase-1-score-from-clean-input.md). Topics it needs to cover: `ProviderProfile` schema, the 4 pure-code validators (license/dea/board/sanctions), score synthesis, dashboard skeleton, the 3 fixture providers, citation stub shape. The Langfuse-v3 vs Jaeger decision can wait until P4 unless trace UI becomes load-bearing earlier.
