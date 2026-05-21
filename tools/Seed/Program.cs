using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PacketReady.Application;
using PacketReady.Application.Scoring.Commands.ComputeReadinessScore;
using PacketReady.Domain;
using PacketReady.Domain.Providers;
using PacketReady.Domain.Scoring;
using PacketReady.Infrastructure;
using PacketReady.Infrastructure.Persistence;
using PacketReady.Seed;

// Slim DI graph — no Anthropic SDK, no OTel. AddPersistence gives us DbContext +
// IAppDbContext + IUnitOfWork + IAuditWriter; AddApplication gives us validators
// and the MediatR pipeline. The FixedClock override below replaces the singleton
// TimeProvider so seed-resolved fixture dates and handler `today` calculations
// share one anchor (otherwise they can drift by a day at UTC midnight).
var builder = Host.CreateApplicationBuilder(args);

var connStr = builder.Configuration["DB_CONNECTION_STRING"]
    ?? throw new InvalidOperationException(
        "DB_CONNECTION_STRING is not configured. Run `set -a && source .env && set +a` first.");

// Refuse to run against anything that doesn't look like a developer database.
// The seed wipes `providers` (CASCADE clears `readiness_scores`); a misrouted
// .env pointing at staging or prod would be unrecoverable. To opt out for an
// intentional remote-dev wipe, set PACKETREADY_SEED_ALLOW=1.
if (!IsLocalConnection(connStr) && builder.Configuration["PACKETREADY_SEED_ALLOW"] != "1")
{
    throw new InvalidOperationException(
        $"Refusing to seed: DB_CONNECTION_STRING does not look like a local DB " +
        $"(no localhost/127.0.0.1/sslmode=disable). Set PACKETREADY_SEED_ALLOW=1 " +
        $"to override for an intentional remote-dev wipe.");
}

builder.Services.AddPersistence(builder.Configuration);
builder.Services.AddApplication();
builder.Services.AddMediatR(cfg =>
    cfg.RegisterServicesFromAssembly(typeof(ComputeReadinessScoreCommand).Assembly));

// One anchor for the whole run: placeholder resolution and the handler's TimeProvider
// see the same `now`. Last DI registration wins, so this replaces the TimeProvider.System
// from AddApplication. Without this the seed can resolve a date as "today-30d" while
// the validator sees it as "today-29d" if the call crosses UTC midnight.
var nowUtc = DateTimeOffset.UtcNow;
builder.Services.AddSingleton<TimeProvider>(new FixedClock(nowUtc));

using var host = builder.Build();
var ct = CancellationToken.None;

// Fixtures are looked up relative to the Seed binary's working dir. By default
// `dotnet run --project tools/Seed` runs from the repo root, so this resolves.
var fixturesDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "evals", "fixtures"));
if (!Directory.Exists(fixturesDir))
    throw new DirectoryNotFoundException(
        $"Fixtures dir not found at {fixturesDir}. Run from repo root.");

var fixtureFiles = Directory.GetFiles(fixturesDir, "provider-*.json").OrderBy(p => p).ToList();
Console.WriteLine($"Found {fixtureFiles.Count} fixture(s) under {fixturesDir}");

// One scope for the whole seed. Wipe + insert + per-fixture compute all share it.
using var scope = host.Services.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<PacketReadyDbContext>();
var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

// Wipe providers; CASCADE on FK clears readiness_scores too.
// audit_events is append-only by trigger and accumulates across seeds — by design.
Console.WriteLine("Wiping providers + readiness_scores...");
var deleted = await db.Providers.ExecuteDeleteAsync(ct);
Console.WriteLine($"  deleted {deleted} provider(s); CASCADE cleared their scores.");

var results = new List<(string label, int expected, int actual, Tier expectedTier, Tier actualTier, bool ok)>();

foreach (var path in fixtureFiles)
{
    var name = Path.GetFileNameWithoutExtension(path);
    Console.WriteLine($"\n→ {name}");

    var rawJson = await File.ReadAllTextAsync(path, ct);
    var resolved = DatePlaceholderResolver.Resolve(rawJson, nowUtc);

    var fixture = JsonSerializer.Deserialize<FixtureModel>(resolved, DomainJson.Options)
        ?? throw new InvalidOperationException($"Failed to parse {name}");
    if (!string.IsNullOrWhiteSpace(fixture.Notes))
        Console.WriteLine($"  notes: {fixture.Notes}");

    // Provider.Create runs ProviderProfile.Validate against nowUtc and throws on
    // any shape violation before the DB sees the row.
    var provider = Provider.Create(fixture.Profile, nowUtc);
    db.Providers.Add(provider);
    await db.SaveChangesAsync(ct);

    var dto = await mediator.Send(new ComputeReadinessScoreCommand(provider.Id), ct);

    var ok = dto.Score == fixture.ExpectedScore && dto.Tier == fixture.ExpectedTier;
    var marker = ok ? "✓" : "✗";
    Console.WriteLine(
        $"  {marker} {fixture.Label}: score={dto.Score} (expected {fixture.ExpectedScore}), " +
        $"tier={dto.Tier} (expected {fixture.ExpectedTier}), " +
        $"{dto.CriticalCount}C/{dto.MajorCount}M/{dto.MinorCount}Min, {dto.Issues.Count} issues");

    results.Add((fixture.Label, fixture.ExpectedScore, dto.Score, fixture.ExpectedTier, dto.Tier, ok));
}

Console.WriteLine("\n──────────────────────────────────────────────");
Console.WriteLine("Seed summary:");
foreach (var r in results)
{
    var marker = r.ok ? "✓" : "✗";
    Console.WriteLine($"  {marker} {r.label,-25} expected={r.expected,3} actual={r.actual,3}  expected={r.expectedTier,-6} actual={r.actualTier}");
}
Console.WriteLine("──────────────────────────────────────────────");

var failed = results.Count(r => !r.ok);
if (failed > 0)
{
    Console.Error.WriteLine($"FAIL: {failed} fixture(s) did not match expected score/tier.");
    Environment.Exit(1);
}

Console.WriteLine($"All {results.Count} fixtures matched expected scores. ✓");

static bool IsLocalConnection(string connStr)
{
    var s = connStr.ToLowerInvariant();
    return s.Contains("host=localhost")
        || s.Contains("host=127.0.0.1")
        || s.Contains("server=localhost")
        || s.Contains("server=127.0.0.1");
}

// Trivially small TimeProvider shim. The Microsoft.Extensions.TimeProvider.Testing
// package's FakeTimeProvider would also work but is tagged "Testing" and would pull
// a test-flavored package into a tools binary; this is three lines.
file sealed class FixedClock(DateTimeOffset nowUtc) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => nowUtc;
}
