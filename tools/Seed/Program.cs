using System.Runtime.CompilerServices;
using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PacketReady.Application;
using PacketReady.Application.Prompts;
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

// P4 LLM validators (IdentityCoherence, NpiTaxonomyMatch) take IChatClient
// in their constructor. The current fixtures don't populate per-doc fullName
// or License.TaxonomyCode, so both validators short-circuit before any
// network call. We register a throwing stub here so DI can construct them
// while making sure a fixture that *does* trigger the path fails loudly
// instead of fabricating a deterministic-looking but fake LLM response.
// If/when seed fixtures grow per-doc names or taxonomy codes, swap this for
// the real AnthropicClient wiring (see Infrastructure/DependencyInjection.cs
// AddInfrastructure) — or run those fixtures via the API host.
builder.Services.AddSingleton<IChatClient, NoopChatClient>();
// PromptLoader is embedded-resource lookup, no LLM dep; safe even though
// AddInfrastructure is the usual owner.
builder.Services.AddSingleton<IPromptLoader, PromptLoader>();

// One anchor for the whole run: placeholder resolution and the handler's TimeProvider
// see the same `now`. Last DI registration wins, so this replaces the TimeProvider.System
// from AddApplication. Without this the seed can resolve a date as "today-30d" while
// the validator sees it as "today-29d" if the call crosses UTC midnight.
var nowUtc = DateTimeOffset.UtcNow;
builder.Services.AddSingleton<TimeProvider>(new FixedClock(nowUtc));

using var host = builder.Build();
var ct = CancellationToken.None;

// `--demo` mode (P6 task 2): stage exactly 3 providers at fixed Guids so the
// demo script's URLs never change between rehearsals. Without the flag the
// seed walks every fixture in `evals/fixtures/` and assigns fresh Guids.
var isDemo = args.Contains("--demo");

// Fixed Guids for the three demo providers. Map keyed by fixture filename so
// the existing per-fixture loop stays the single code path. Touching these
// breaks the demo script — coordinate with `docs/demo-script.md`.
var demoIds = new Dictionary<string, Guid>(StringComparer.Ordinal)
{
    ["provider-green.json"]  = new("11111111-1111-1111-1111-111111111111"),
    ["provider-yellow.json"] = new("22222222-2222-2222-2222-222222222222"),
    ["provider-red.json"]    = new("33333333-3333-3333-3333-333333333333"),
};

// Fixtures are looked up relative to the Seed binary's working dir. By default
// `dotnet run --project tools/Seed` runs from the repo root, so this resolves.
var fixturesDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "evals", "fixtures"));
if (!Directory.Exists(fixturesDir))
    throw new DirectoryNotFoundException(
        $"Fixtures dir not found at {fixturesDir}. Run from repo root.");

var fixtureFiles = Directory.GetFiles(fixturesDir, "provider-*.json")
    .OrderBy(p => p, StringComparer.Ordinal)
    .Where(p => !isDemo || demoIds.ContainsKey(Path.GetFileName(p)))
    .ToList();

if (isDemo && fixtureFiles.Count != demoIds.Count)
{
    var missing = demoIds.Keys
        .Except(fixtureFiles.Select(Path.GetFileName))
        .ToList();
    throw new InvalidOperationException(
        $"Demo mode expected {demoIds.Count} fixtures, found {fixtureFiles.Count}. " +
        $"Missing: {string.Join(", ", missing)}");
}

Console.WriteLine(isDemo
    ? $"Demo mode: staging {fixtureFiles.Count} fixed-Guid provider(s) under {fixturesDir}"
    : $"Found {fixtureFiles.Count} fixture(s) under {fixturesDir}");

// Demo mode also writes Document + DocumentExtraction + DocumentUploaded
// audit rows so the score handler's aggregator (which reads from extractions,
// not Provider.profile) can populate License/DEA/BoardCert/Malpractice. The
// blob store lives under apps/api/Api/blob-store by default; ops can override
// it with BLOB_STORE_ROOT identical to the API's own resolution rule. The
// source PDFs come from the eval dataset — they're real PDFs so the
// IssueCard's PDF preview has something to render in beat 2.
var repoRoot = Directory.GetCurrentDirectory();
var demoBlobStoreRoot = builder.Configuration["BLOB_STORE_ROOT"];
if (string.IsNullOrWhiteSpace(demoBlobStoreRoot))
    demoBlobStoreRoot = Path.Combine(repoRoot, "apps", "api", "Api", "blob-store");
else if (!Path.IsPathRooted(demoBlobStoreRoot))
    demoBlobStoreRoot = Path.Combine(repoRoot, demoBlobStoreRoot);
var demoSourcePdfDir = Path.Combine(repoRoot, "evals", "dataset", "packet-001-clean-anderson");
if (isDemo && !Directory.Exists(demoSourcePdfDir))
    throw new DirectoryNotFoundException(
        $"Demo source PDFs not found at {demoSourcePdfDir}. Need the eval dataset checked out.");

// One scope for the whole seed. Wipe + insert + per-fixture compute all share it.
using var scope = host.Services.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<PacketReadyDbContext>();
var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
var auditWriter = scope.ServiceProvider.GetRequiredService<PacketReady.Application.Audit.IAuditWriter>();

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
    // any shape violation before the DB sees the row. payerId from the fixture
    // (or DefaultPayerId when omitted) flows in here; P4 task 5's 50-packet
    // generator will start emitting fixtures with explicit payerId for ~50/50
    // balance across the two committed payer YAMLs.
    //
    // Demo mode (P6 task 2) routes through Provider.CreateForTesting so the
    // 3 demo providers land at fixed Guids — the demo script's URLs depend
    // on these never changing.
    var provider = isDemo
        ? Provider.CreateForTesting(
            demoIds[Path.GetFileName(path)], fixture.Profile, nowUtc, fixture.PayerId)
        : Provider.Create(fixture.Profile, nowUtc, fixture.PayerId);
    db.Providers.Add(provider);
    await db.SaveChangesAsync(ct);

    if (isDemo)
    {
        // Materialize the documents+extractions lane so the score handler's
        // aggregator path produces real Issues/citations and the dashboard's
        // IssueCard side panel has a PDF to render.
        await DemoDocumentSeeder.SeedAsync(
            db, auditWriter, provider, fixture.Profile, nowUtc,
            demoBlobStoreRoot, demoSourcePdfDir, ct);
        Console.WriteLine($"  staged 4 documents + extractions under blob root {demoBlobStoreRoot}");
    }

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

if (isDemo)
{
    Console.WriteLine();
    Console.WriteLine("Demo URLs (paste into the recording browser):");
    foreach (var (filename, id) in demoIds.OrderBy(kv => kv.Value))
    {
        var label = Path.GetFileNameWithoutExtension(filename).Replace("provider-", "");
        Console.WriteLine($"  {label,-7} → http://localhost:3001/providers/{id}");
    }
}

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

// Throws on any actual chat call. The Seed binary's fixtures are deterministic
// and shouldn't trigger an LLM round-trip; if they do, surface the misconfig
// instead of silently returning {} and letting validators emit zero issues.
file sealed class NoopChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(
            "Seed binary reached an IChatClient call. Demo fixtures must short-circuit " +
            "before any LLM validator hits the wire. Either back the fixture off the LLM path " +
            "or run it through the API host (which wires the real AnthropicClient).");

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        throw new InvalidOperationException(
            "Seed binary reached an IChatClient streaming call. Same constraint as the sync path.");
#pragma warning disable CS0162 // Unreachable — present so the compiler knows this is an IAsyncEnumerable.
        yield break;
#pragma warning restore CS0162
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
