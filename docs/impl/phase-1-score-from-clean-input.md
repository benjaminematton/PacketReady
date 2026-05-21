# Phase 1 — Score from Clean Input

> Hand-curated `ProviderProfile` → readiness score with cited issues → rendered in a dashboard. No extraction yet; the input is JSON.

| | |
|---|---|
| **Parent** | [build-plan.md](../build-plan.md) — Phase 1 row |
| **Goal** | Prove the score logic + dashboard before any extractor exists. |
| **Status** | Not started |
| **Depends on** | [Phase 0](./phase-0-walking-skeleton.md) — closed 2026-05-21 |

---

## Definition of done

Three fixture providers checked in at `evals/fixtures/*.json` produce three distinct scores in the dashboard:

- [ ] **Dr. Green** — all valid → **score == 100** (0 issues), green badge.
- [ ] **Dr. Yellow** — 1 Critical + 1 Major + 1 Minor → **score == 62** (`100 − 25 − 10 − 3`), yellow badge.
- [ ] **Dr. Red** — 2 Critical + 1 Major + 2 Minor → **score == 34** (`100 − 25 − 25 − 10 − 3 − 3`), red badge.
- [ ] Clicking any provider opens a side panel listing every Issue with severity, message, remediation, and a citation stub (`source: validator_name, extracted_value: …`).
- [ ] `POST /api/providers/{id}/score` returns the same `ReadinessScore` shape the dashboard renders.
- [ ] An `AuditEvent` of `event_type='ScoreComputed'` lands in Postgres for every score computation.
- [ ] `dotnet test` passes — at least 4 validator tests + 1 synthesizer test (rubric sanity).

If all eight boxes check, Phase 1 is closed. Move to [Phase 2 — Eval harness + 5 packets](./phase-2-eval-harness.md).

---

## Stack additions

| Layer | Addition | Version | Why |
|---|---|---|---|
| Frontend | Next.js (App Router) | 15.x | Dashboard skeleton. RSC for list, client for drill-in panel. |
| Frontend | React | 19.x | Bundled with Next 15. |
| Frontend | shadcn/ui + Tailwind | latest | Card, Badge, Sheet, Button. Zero design effort. |
| Frontend | TypeScript | 5.x | Strict. |
| Backend | (no new packages) | — | Validators are pure C#; no LLM in P1. |

CORS will be enabled on the API for `http://localhost:3001` (dashboard dev port). Phase 5 will tighten when intake portal lands.

---

## Project layout deltas

```
PacketReady/
├── apps/
│   ├── api/                        (existing)
│   │   ├── Domain/
│   │   │   ├── Providers/          NEW
│   │   │   │   ├── Provider.cs
│   │   │   │   ├── ProviderProfile.cs
│   │   │   │   ├── LicenseInfo.cs
│   │   │   │   ├── DeaInfo.cs
│   │   │   │   ├── BoardCertInfo.cs
│   │   │   │   └── SanctionsResult.cs
│   │   │   └── Scoring/            NEW
│   │   │       ├── Severity.cs
│   │   │       ├── Tier.cs
│   │   │       ├── Issue.cs
│   │   │       ├── Citation.cs
│   │   │       └── ReadinessScore.cs
│   │   ├── Application/
│   │   │   └── Scoring/            NEW
│   │   │       ├── Validators/
│   │   │       │   ├── IValidator.cs
│   │   │       │   ├── LicenseStatusValidator.cs
│   │   │       │   ├── DeaStatusValidator.cs
│   │   │       │   ├── BoardCertificationValidator.cs
│   │   │       │   └── SanctionsCheckValidator.cs
│   │   │       ├── ScoreSynthesizer.cs
│   │   │       └── Commands/ComputeReadinessScore/
│   │   │           ├── ComputeReadinessScoreCommand.cs
│   │   │           └── ComputeReadinessScoreCommandHandler.cs
│   │   ├── Infrastructure/
│   │   │   └── Persistence/Configurations/
│   │   │       ├── ProviderConfiguration.cs       NEW
│   │   │       └── ReadinessScoreConfiguration.cs NEW
│   │   └── Api/
│   │       └── Endpoints/
│   │           └── ProviderEndpoints.cs           NEW
│   └── dashboard/                  NEW (Next.js 15)
│       ├── package.json
│       ├── next.config.ts
│       ├── tailwind.config.ts
│       ├── tsconfig.json
│       ├── app/
│       │   ├── layout.tsx
│       │   ├── page.tsx                 (redirects to /providers)
│       │   └── providers/
│       │       ├── page.tsx             (list)
│       │       └── [id]/
│       │           └── page.tsx         (detail + side panel)
│       ├── lib/
│       │   ├── api.ts                   (typed fetch client)
│       │   └── types.ts                 (mirror of API DTOs)
│       └── components/
│           ├── score-badge.tsx
│           ├── issue-card.tsx
│           └── issue-side-panel.tsx
└── evals/
    └── fixtures/                   NEW
        ├── provider-green.json
        ├── provider-yellow.json
        └── provider-red.json
```

---

## Domain shapes (file-by-file)

### `Domain/Providers/Provider.cs`

P1 minimum: id + created_at + profile blob. P5 will add `email`, `status` (FSM), `intake_session_id`. Keep Provider thin.

```csharp
namespace PacketReady.Domain.Providers;

public class Provider
{
    public Guid Id { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>JSON-serialized <see cref="ProviderProfile"/>. Validated by <see cref="ProviderProfile.Validate"/> in <see cref="Create"/>.</summary>
    public string ProfileJson { get; private set; } = "{}";

    private Provider() { }

    public static Provider Create(ProviderProfile profile, DateTimeOffset? now = null)
    {
        // Fail fast at the write boundary, not when a validator runs. A bad NPI or
        // a future DOB would otherwise surface as a misleading downstream Issue.
        ProviderProfile.Validate(profile, DateOnly.FromDateTime((now ?? DateTimeOffset.UtcNow).UtcDateTime));

        return new Provider
        {
            Id = Guid.NewGuid(),
            CreatedAt = now ?? DateTimeOffset.UtcNow,
            ProfileJson = System.Text.Json.JsonSerializer.Serialize(profile),
        };
    }

    public ProviderProfile GetProfile() =>
        System.Text.Json.JsonSerializer.Deserialize<ProviderProfile>(ProfileJson)
        ?? throw new InvalidOperationException("Profile JSON is invalid.");
}
```

### `Domain/Providers/ProviderProfile.Validate`

A static method on `ProviderProfile`. Cheap checks; throws `ArgumentException` on the first failure. Purpose is to keep bad shape out of the DB and out of validator inputs — not to enforce business rules (that's the validator suite's job).

```csharp
public sealed record ProviderProfile(/* ...fields above... */)
{
    private static readonly System.Text.RegularExpressions.Regex NpiRegex = new(@"^\d{10}$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex StateRegex = new(@"^[A-Z]{2}$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly DateOnly MinPlausibleDate = new(1900, 1, 1);

    // Takes DateTimeOffset (not DateOnly) so callers can pass the same `now`
    // they use elsewhere in a write path without an extra conversion. Internally
    // projects to a DateOnly `today` for date comparisons.
    public static void Validate(ProviderProfile p, DateTimeOffset nowUtc)
    {
        var today = DateOnly.FromDateTime(nowUtc.UtcDateTime);

        if (string.IsNullOrWhiteSpace(p.FullName))
            throw new ArgumentException("FullName is required.", nameof(p));

        if (!NpiRegex.IsMatch(p.Npi))
            throw new ArgumentException($"Npi must be exactly 10 digits; got '{p.Npi}'.", nameof(p));

        if (!StateRegex.IsMatch(p.CredentialingState))
            throw new ArgumentException($"CredentialingState must be 2 uppercase letters; got '{p.CredentialingState}'.", nameof(p));

        if (p.DateOfBirth > today || p.DateOfBirth <= MinPlausibleDate)
            throw new ArgumentException($"DateOfBirth implausible: {p.DateOfBirth}.", nameof(p));

        if (p.License is { } lic)
        {
            if (string.IsNullOrWhiteSpace(lic.Number)) throw new ArgumentException("License.Number is required.");
            if (!StateRegex.IsMatch(lic.State))         throw new ArgumentException($"License.State invalid: '{lic.State}'.");
            if (lic.IssueDate > lic.ExpiryDate)         throw new ArgumentException("License.IssueDate after ExpiryDate.");
        }

        if (p.Dea is { } dea)
        {
            if (string.IsNullOrWhiteSpace(dea.Number)) throw new ArgumentException("Dea.Number is required.");
            if (dea.ExpiryDate <= MinPlausibleDate)     throw new ArgumentException("Dea.ExpiryDate implausible.");
        }

        if (p.BoardCert is { } bc)
        {
            if (string.IsNullOrWhiteSpace(bc.Specialty)) throw new ArgumentException("BoardCert.Specialty is required.");
            if (bc.IssueDate > bc.ExpiryDate)            throw new ArgumentException("BoardCert.IssueDate after ExpiryDate.");
        }

        if (p.Sanctions is { } s)
        {
            if (s.CheckedAt <= MinPlausibleDate || s.CheckedAt > today.AddDays(1))
                throw new ArgumentException($"Sanctions.CheckedAt implausible: {s.CheckedAt}.");
        }
    }
}
```

**Scope discipline:** these are shape checks, not policy. "License expired" is a validator concern, not a `Validate` concern — we want the validator to surface that as a Critical Issue, not have `Provider.Create` throw before it gets the chance.

### `Domain/Providers/ProviderProfile.cs`

A value object. Not stored as separate columns — embedded in `Provider.ProfileJson`. Phase 3 will rebuild this from extraction rows instead of taking it as input.

```csharp
namespace PacketReady.Domain.Providers;

public sealed record ProviderProfile(
    string FullName,
    DateOnly DateOfBirth,
    string Npi,
    string CredentialingState,    // 2-letter state code, the state we're credentialing FOR
    LicenseInfo? License,
    DeaInfo? Dea,
    BoardCertInfo? BoardCert,
    SanctionsResult? Sanctions);

public sealed record LicenseInfo(
    string Number,
    string State,
    DateOnly IssueDate,
    DateOnly ExpiryDate,
    LicenseStatus Status);

public enum LicenseStatus { Active, Suspended, Expired, Unknown }

public sealed record DeaInfo(
    string Number,
    DateOnly ExpiryDate,
    DeaStatus Status,
    IReadOnlyList<DeaSchedule> Schedules);

public enum DeaStatus { Unknown = 0, Active = 1, Inactive = 2, Expired = 3 }

// Controlled-substance schedules a DEA registration may cover. Schedule I has no
// accepted medical use, so providers do not hold DEA for it; not modeled.
public enum DeaSchedule { II = 2, III = 3, IV = 4, V = 5 }

public sealed record BoardCertInfo(
    string Board,             // e.g. "ABIM"
    string Specialty,
    DateOnly IssueDate,
    DateOnly ExpiryDate,
    BoardCertStatus Status);

public enum BoardCertStatus { Active, Expired, Unknown }

public sealed record SanctionsResult(
    bool OigClean,
    bool SamClean,
    DateTimeOffset CheckedAt);   // not DateOnly — re-checks within a day are meaningful
```

### `Domain/Scoring/Issue.cs`

```csharp
namespace PacketReady.Domain.Scoring;

public sealed record Issue(
    string Validator,          // "license_status", "dea_status", etc.
    Severity Severity,
    string Message,
    string Remediation,
    IReadOnlyList<Citation> Citations);

public enum Severity { Minor = 1, Major = 2, Critical = 3 }   // ordinals so DESC sort works
```

### `Domain/Scoring/Citation.cs`

P1 has no source documents — citations carry the validator and the extracted value only. P3 will add `DocumentId`, `Page`, `Bbox`.

```csharp
namespace PacketReady.Domain.Scoring;

public sealed record Citation(
    string SourceValidator,
    string ExtractedValue,
    // P3 additions, nullable until then:
    Guid? DocumentId = null,
    int? Page = null,
    BoundingBox? Bbox = null);

// Axis-aligned bbox in normalized PDF page coordinates (top-left origin, 0..1 on
// each axis). Explicit field names avoid the xywh/x1y1x2y2 ambiguity a raw
// double[] would carry.
public sealed record BoundingBox(double X1, double Y1, double X2, double Y2);
```

### `Domain/Scoring/ReadinessScore.cs`

```csharp
namespace PacketReady.Domain.Scoring;

public class ReadinessScore
{
    public Guid Id { get; private set; }
    public Guid ProviderId { get; private set; }
    public int Score { get; private set; }
    public Tier Tier { get; private set; }              // derived from Score, never passed in
    public int CriticalCount { get; private set; }
    public int MajorCount { get; private set; }
    public int MinorCount { get; private set; }
    public string IssuesJson { get; private set; } = "[]";    // serialized List<Issue> via DomainJson.Options
    public DateTimeOffset ComputedAt { get; private set; }

    private ReadinessScore() { }

    // No `tier` parameter. Tier is derived from `score` via TierExtensions.FromScore
    // (defined alongside the Tier enum). Callers cannot smuggle in a contradictory
    // pair — if you have a score, you have its tier.
    public static ReadinessScore Create(
        Guid providerId, int score, IReadOnlyList<Issue> issues, DateTimeOffset now)
    {
        if (score is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(score), score, "Score must be 0..100.");

        return new ReadinessScore
        {
            Id = Guid.NewGuid(),
            ProviderId = providerId,
            Score = score,
            Tier = TierExtensions.FromScore(score),
            CriticalCount = issues.Count(i => i.Severity == Severity.Critical),
            MajorCount = issues.Count(i => i.Severity == Severity.Major),
            MinorCount = issues.Count(i => i.Severity == Severity.Minor),
            IssuesJson = System.Text.Json.JsonSerializer.Serialize(issues, DomainJson.Options),
            ComputedAt = now,
        };
    }
}

public enum Tier { Red, Yellow, Green }
// Tier is a categorical UI label. Don't rely on enum-order for SQL sorts —
// `tier` is stored as TEXT in Postgres and ORDER BY would sort alphabetically
// (Green, Red, Yellow), not by severity. Use `score` (numeric) as the sort key:
// ORDER BY score ASC for worst-first, DESC for best-first.

public static class TierExtensions
{
    /// <summary>Single source of truth for the score → tier mapping.</summary>
    public static Tier FromScore(int score) => score switch
    {
        >= 85 => Tier.Green,
        >= 60 => Tier.Yellow,
        _ => Tier.Red,
    };
}
```

### `Application/Scoring/Validators/IValidator.cs`

```csharp
namespace PacketReady.Application.Scoring.Validators;

using PacketReady.Domain.Providers;
using PacketReady.Domain.Scoring;

public interface IValidator
{
    /// <summary>Stable name, stored on every Issue this validator emits and used for citation cross-ref.</summary>
    string Name { get; }

    /// <summary>
    /// Emits zero or more <see cref="Issue"/>s. Empty list means "pass." A single validator
    /// may emit multiple Issues — e.g. LicenseStatusValidator emits both a Critical (expired)
    /// and a Major (state mismatch) on the same license. No short-circuiting: surface every
    /// detectable problem so the score and the side-panel match what's actually wrong.
    /// </summary>
    Task<IReadOnlyList<Issue>> RunAsync(ProviderProfile profile, CancellationToken ct);
}
```

There is deliberately no `ValidatorResult` middle type. `Issue` is the single output. An empty list is the pass signal — clearer than a nullable severity.

### `Application/Scoring/Validators/LicenseStatusValidator.cs`

Each validator is < 50 lines. **Important shape rules:**

1. **Collect into a list — no short-circuit.** A single license can be both expired (Critical) AND in the wrong state (Major). Emit both. The side-panel shows what's wrong; hiding a Major because a Critical fired first is a bug, not a feature.
2. **Use `this.Name` when constructing each Issue.** Never inline the literal `"license_status"` inside the method body — a copy-paste into `DeaStatusValidator` would leave the wrong attribution silently, and tests pass anyway because severity counts are right.
3. **Expiry boundary is `<`, not `<=`.** Industry convention: a license is valid through the expiry date inclusive. "Expires today" is still valid; "expired" means `expiry_date < today`.

```csharp
namespace PacketReady.Application.Scoring.Validators;

using PacketReady.Domain.Providers;
using PacketReady.Domain.Scoring;

public sealed class LicenseStatusValidator(TimeProvider clock) : IValidator
{
    public string Name => "license_status";

    public Task<IReadOnlyList<Issue>> RunAsync(ProviderProfile p, CancellationToken ct)
    {
        var issues = new List<Issue>();
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        if (p.License is null)
        {
            issues.Add(new Issue(
                Validator: Name,
                Severity: Severity.Critical,
                Message: "No license on file — required for credentialing.",
                Remediation: "Provider must upload an active state medical license.",
                Citations: Array.Empty<Citation>()));
            return Task.FromResult<IReadOnlyList<Issue>>(issues);
        }

        var lic = p.License;
        IReadOnlyList<Citation> cite = new[]
        {
            new Citation(Name, $"{lic.State} {lic.Number} status={lic.Status} expires={lic.ExpiryDate:yyyy-MM-dd}"),
        };

        if (lic.Status != LicenseStatus.Active)
            issues.Add(new Issue(Name, Severity.Critical,
                $"License status is {lic.Status}; must be Active.",
                "Resolve license status with the issuing board.", cite));

        // industry convention: license is valid through the expiry date inclusive.
        // "expires today" is still valid; "expired" means expiry_date < today.
        if (lic.ExpiryDate < today)
            issues.Add(new Issue(Name, Severity.Critical,
                $"License expired on {lic.ExpiryDate:yyyy-MM-dd}.",
                "Renew with the state board before submission.", cite));

        if (lic.State != p.CredentialingState)
            issues.Add(new Issue(Name, Severity.Major,
                $"License is in {lic.State}; credentialing for {p.CredentialingState}.",
                "Confirm the provider is licensed in the credentialing state, or update the target.", cite));

        // Only emit the renewal-window Minor when the license is otherwise valid.
        // No point telling someone "renews in 12 days" when we've already flagged it expired.
        if (lic.ExpiryDate >= today
            && (lic.ExpiryDate.DayNumber - today.DayNumber) < 30)
            issues.Add(new Issue(Name, Severity.Minor,
                $"License expires in {lic.ExpiryDate.DayNumber - today.DayNumber} days.",
                "Renewal recommended before payer submission.", cite));

        return Task.FromResult<IReadOnlyList<Issue>>(issues);
    }
}
```

`DeaStatusValidator` and `BoardCertificationValidator` follow the same shape: same `<` boundary for the expiry check, same multi-emit, same `this.Name` rule. `SanctionsCheckValidator` has its own ladder — defined explicitly below.

### `Application/Scoring/Validators/SanctionsCheckValidator.cs`

OIG and SAM are checked **independently**. Each source can fire its own Issue. This is what lets the red fixture pick up two Minors from one validator — staleness on both sources at once.

| Condition (per source: OIG, SAM) | Severity | Message |
|---|---|---|
| `OigClean == false` | Critical | "OIG sanction on file." |
| `SamClean == false` | Critical | "SAM debarment on file." |
| `Sanctions == null` | Critical | "No sanctions check on file." |
| `today − CheckedAt ≥ 365 days` | Major | "Sanctions check is over a year old." |
| `90 days ≤ today − CheckedAt < 365 days` | Minor | "{OIG\|SAM} check is stale (> 90 days)." (per source) |
| Otherwise | (pass) | — |

Implementation note: `Sanctions == null` is one Issue (we don't know which source failed; the gap is the whole check). Staleness emits **two** Issues when both OIG and SAM share the same `CheckedAt` and that timestamp is stale — each with a per-source message — because the side-panel UX is per-source citation.

(Same multi-emit + `this.Name` rules as LicenseStatusValidator.)

### `Application/Scoring/ScoreSynthesizer.cs`

```csharp
namespace PacketReady.Application.Scoring;

using PacketReady.Domain.Scoring;

public static class ScoreSynthesizer
{
    public static int Compute(IEnumerable<Issue> issues)
    {
        var score = 100;
        foreach (var i in issues)
        {
            score -= i.Severity switch
            {
                Severity.Critical => 25,
                Severity.Major => 10,
                Severity.Minor => 3,
                _ => 0,
            };
        }
        return Math.Max(0, score);
    }
}
```

Tier mapping lives on `TierExtensions.FromScore` (next to the Tier enum), not here — single source of truth. `ReadinessScore.Create` calls `TierExtensions.FromScore(score)` internally; handlers never compute the tier themselves.

### `Application/Scoring/Commands/ComputeReadinessScore/...`

The handler:
1. Loads `Provider` by id (FAIL 404 if missing).
2. Resolves `IEnumerable<IValidator>` via DI.
3. Runs all validators in parallel via `Task.WhenAll`. Each returns `IReadOnlyList<Issue>` (zero or more).
4. Flattens with `SelectMany`, sorts by `Severity DESC` then by `Validator` name for stable ordering.
5. Computes score via `ScoreSynthesizer.Compute(issues)`.
6. Builds `ReadinessScore.Create(providerId, score, issues, now)` — tier is derived inside `Create` via `TierExtensions.FromScore(score)`; no separate `TierFor` call. Adds to DbContext.
7. Stages an `AuditEvent` (`AuditEventType.ScoreComputed`) via `IAuditWriter.Stage`.
8. `_uow.SaveChangesAsync(ct)`.
9. Returns a `ReadinessScoreDto`.

### `Api/Endpoints/ProviderEndpoints.cs`

Three endpoints:
- `GET /api/providers` — list all providers (Phase 1: just `[{id, full_name, latest_score?}]`).
- `GET /api/providers/{id}` — provider + latest score + issues.
- `POST /api/providers/{id}/score` — compute fresh score.

P1 doesn't have a `POST /api/providers` (create) — fixtures are seeded directly. P5 introduces creation.

---

## Migration

One new migration: `AddProvidersAndScores`.

```sql
CREATE TABLE providers (
  id          uuid PRIMARY KEY,
  created_at  timestamptz NOT NULL,
  profile     jsonb NOT NULL
);

CREATE TABLE readiness_scores (
  id              uuid PRIMARY KEY,
  provider_id     uuid NOT NULL REFERENCES providers(id),
  score           int NOT NULL CHECK (score BETWEEN 0 AND 100),
  tier            text NOT NULL CHECK (tier IN ('Green','Yellow','Red')),
  critical_count  int NOT NULL,
  major_count     int NOT NULL,
  minor_count     int NOT NULL,
  issues          jsonb NOT NULL,
  computed_at     timestamptz NOT NULL
);

CREATE INDEX ix_readiness_scores_provider_computed
  ON readiness_scores(provider_id, computed_at DESC);
```

`readiness_scores` is append-only by convention (we never UPDATE a score; new compute → new row). No trigger needed — we'd want history if the rubric changes.

---

## Fixtures (`evals/fixtures/`)

Each fixture is a literal `ProviderProfile` JSON — same shape `POST /score` would accept. A seed CLI (`tools/seed-fixtures.csx` or a `dotnet run` command) wipes + inserts. Math below assumes validators emit lists of Issues (per the new `IValidator` shape) so multiple Issues can come from one validator.

### `provider-green.json`

**Score: 100.** Everything valid; no Issues emitted.

- License: Active, in `CredentialingState`, expires > 30 days out.
- DEA: Active, expires > 30 days out, schedules include II–V.
- BoardCert: Active, specialty matches license-state context, expires > 30 days out.
- Sanctions: `OigClean=true`, `SamClean=true`, `CheckedAt` within 90 days.

Total: `100 − 0 = 100`. ✓

### `provider-yellow.json`

**Score: 62.** 1 Critical + 1 Major + 1 Minor.

- License: **expired** *and* in the wrong state (one validator emits **two** Issues — Critical (expired) + Major (state mismatch)).
- DEA: Active but expires in `< 30 days` → Minor (renewal window).
- BoardCert: clean.
- Sanctions: clean and current.

Total: `100 − 25 (License Critical) − 10 (License Major) − 3 (DEA Minor) = 62`. ✓

### `provider-red.json`

**Score: 34.** 2 Critical + 1 Major + 2 Minor.

- License: **expired** → Critical.
- DEA: **expired** → Critical.
- BoardCert: **expired** → Major.
- Sanctions: clean but **both OIG and SAM `CheckedAt` are stale > 90 days** → two Minors (one per source).

Total: `100 − 25 (License) − 25 (DEA) − 10 (BoardCert) − 3 (OIG stale) − 3 (SAM stale) = 34`. ✓

The red fixture exercises the multi-Issue-per-validator behavior on Sanctions (two Minors). The yellow fixture exercises it on License (Critical + Major). The green fixture exercises the empty-list pass path. Between them, every Issue-emission branch in the four validators has at least one fixture test.

---

## Dashboard skeleton

**Decision: separate Next.js project, not embedded in the API.** Closer to how Atano's real frontend would deploy, and the .NET API doesn't grow a static-file hosting responsibility.

**Pages:**
- `/providers` — server component, fetches `GET /api/providers`. Renders Card per provider with name + score badge.
- `/providers/[id]` — server component fetches detail; renders Issue list. Side panel is a client component (Radix Sheet) for drill-in.

**Components:**
- `ScoreBadge` — colored pill: green/yellow/red, displays the number.
- `IssueCard` — severity icon + message + remediation + cite stub. Click opens side panel.
- `IssueSidePanel` — full validator metadata, citation list, "Why did we flag this?" placeholder (will link to audit log in P6).

**Styling:** Tailwind v4, no theme work. Default shadcn dark mode honored.

**Bring-up:**
```bash
cd apps/dashboard
npx create-next-app@latest . --typescript --tailwind --app --no-eslint --no-src-dir
npx shadcn@latest init
npx shadcn@latest add card badge sheet button
```

The Next.js dev server runs on `http://localhost:3001` (override default 3000 to avoid Langfuse).

---

## Task order

1. **Domain types.** Write all `Domain/Providers/` and `Domain/Scoring/` files. Implement `ProviderProfile.Validate`. `dotnet build` passes.
2. **Migration.** `dotnet ef migrations add AddProvidersAndScores`. Apply locally. Verify with `\d providers` and `\d readiness_scores`.
3. **Validator interface + 4 implementations.** Each emits `IReadOnlyList<Issue>` (no short-circuit; uses `this.Name`; `<` boundary). Unit test every Issue-emission branch (absent / status / expired / state-mismatch / window) plus a "valid input emits empty list" test per validator.
4. **ScoreSynthesizer.** Unit test the rubric: green/yellow/red boundary cases (84→Yellow, 85→Green, 59→Red, 60→Yellow), floor at 0 on > 4 Critical issues.
5. **ComputeReadinessScore handler.** Wire into MediatR registration. `Task.WhenAll` + `SelectMany` flow.
6. **Extend `AuditEventType`.** Add `public const string ScoreComputed = "ScoreComputed";` to `Domain/Audit/AuditEventType.cs`. Trivial but explicit — the audit-event-type ledger is the runtime contract.
7. **Endpoints.** List, detail, score. CORS configured for `localhost:3001`.
8. **3 fixture JSON files + seed CLI.** A `dotnet run --project tools/Seed` command that wipes + inserts. Each fixture asserts its expected score in a check the seed runs post-insert (catches arithmetic drift).
9. **Next.js project init + shadcn setup.** `apps/dashboard/` builds, blank page renders.
10. **API client (`lib/api.ts`) + types (`lib/types.ts`).** Hand-written types mirror the .NET DTOs.
11. **Provider list page.** Server component, fetches, renders ScoreBadge per row. `ORDER BY score ASC` (worst-first).
12. **Provider detail page.** Server component renders Issue list (sorted Severity DESC). IssueSidePanel as client component.
13. **Gate verification.** Run the 8 checkboxes.

Order matters: 5 depends on 3+4; 6 must land before 5's handler is wired (the constant gets referenced); 8 depends on 2+7; 12 depends on 11.

---

## Risks / open

- **Rubric tuning.** `-25 / -10 / -3` is a guess. Phase 4 has the data (Spearman correlation against hand-labeled tier) to defend or revise. Don't tune in P1; lock the simple version.
- **DateOnly handling across the wire.** System.Text.Json + DateOnly + Postgres jsonb — confirm round-trip. EF Core 10 has built-in support; STJ added native handling. Smoke-test with the green fixture before declaring done.
- **Citation shape evolution.** P3 will add `DocumentId/Page/Bbox`. The current record's optional fields make the additions non-breaking. Don't add a versioning column — the shape is internal.
- **Validators emit multiple Issues per run.** The interface is deliberately list-returning (no short-circuit) so the side-panel surfaces every detectable problem, not just the most severe. Corollary: one slow or buggy validator can produce a flood. Cap at 10 Issues per validator before logging a "validator misconfigured" warning. (Implementation note, not a hard limit; the cap exists to catch infinite-loops or bad data.)
- **Sort key is `score`, not `tier`.** The list view orders by `score` (ascending = worst-first). Postgres `ORDER BY tier` would sort alphabetically (Green, Red, Yellow) — wrong. Tier is a categorical label for UI, not a sort key.
- **Issue serialization in `readiness_scores.issues`.** Stored as JSONB blob, not normalized into a separate `issues` table. Fine for P1; if we need per-issue queries (e.g. "show me every Critical NPI mismatch this week"), normalize in P4.

---

## Out of scope (resist)

- Authentication on dashboard or API.
- Real-time updates / SignalR.
- Pagination on `GET /api/providers`. < 10 fixtures is fine for P1.
- Filter / search UI. P6 problem.
- Editing a profile from the dashboard.
- Re-running just one validator (rebuild-all only).
- Score history view. Storing per-compute rows is enough; rendering history is P6.
- Mobile responsiveness. Laptop only.
- Tests for the Next.js components. The .NET unit tests carry P1's correctness; the UI is just rendering.

---

## What gets written when Phase 1 closes

Append a one-line outcome note to [build-plan.md](../build-plan.md) Status section. Then write `phase-2-eval-harness.md`. Topics: PDF generator toolchain choice (ReportLab vs LaTeX), 5-packet hand-curated dataset shape, `golden.json` schema, eval runner CLI (.NET vs Python), `evals/results/<date>.json` schema, the regression gate (> 2pp drop blocks merge).

The Langfuse-vs-Jaeger decision from Phase 0's closing notes does **not** need to be made before Phase 2 — neither is on the P2 critical path.
