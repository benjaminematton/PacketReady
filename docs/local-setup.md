# Local setup

Everything you need to bring PacketReady up on a fresh machine: prerequisites,
the Phase 0 walking-skeleton smoke test, the repo layout, and the common
dev commands.

## Prerequisites

Docker, .NET 10 SDK, an Anthropic API key.

## Bring-up

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

Port `5xxx` — check the `dotnet run` output for the actual port (typically
5066 HTTPS / 5065 HTTP). The endpoint accepts plain HTTP in dev.

## Phase 0 gate

A `POST /api/ping` returns a JSON payload with `reply`, `model`,
`audit_event_id`, `trace_id`, token counts, and cost. Verify:

- [ ] Audit row in Postgres: `select * from audit_events;` shows one row
  with `event_type = 'PingExecuted'`.
- [ ] Trace at `http://localhost:3000` shows a `ping.invoke` span with
  model + cost rendered.
- [ ] Killing Postgres → API returns 500.
- [ ] Killing Langfuse → API still returns 200 (telemetry is
  fire-and-forget).

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
