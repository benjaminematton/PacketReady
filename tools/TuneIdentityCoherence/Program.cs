using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Anthropic.SDK;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PacketReady.Application.Prompts;
using PacketReady.Application.Providers.Aggregation;
using PacketReady.Application.Scoring.Validators;
using PacketReady.Domain.Scoring;
using PacketReady.TuneIdentityCoherence;

// ============================================================================
// Arg parsing
// ============================================================================

var opts = CliOptions.Parse(args);
opts.Validate();

// ============================================================================
// DI graph — slim: no DB, no extractor zoo, no audit. Just LLM + prompts +
// the validator. The Anthropic key sources from .env (Host.CreateApplicationBuilder
// reads env vars automatically; bash callers source .env first per the Seed
// CLI's convention).
// ============================================================================

var builder = Host.CreateApplicationBuilder(args);

var apiKey = builder.Configuration["ANTHROPIC_API_KEY"]
    ?? throw new InvalidOperationException(
        "ANTHROPIC_API_KEY is not configured. Run `set -a && source .env && set +a` first.");

builder.Services.AddLogging(b => b.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
}));

// PromptLoader wraps the embedded resources; for --prompt-path overrides the
// CLI substitutes a FileSystemPromptLoader that reads off disk for ONE key
// and falls back to the embedded loader otherwise.
var embeddedLoader = new PromptLoader();
IPromptLoader promptLoader = opts.PromptPath is null
    ? embeddedLoader
    : new FileSystemOverridePromptLoader(
        embeddedLoader,
        overrideKey: PromptKeys.IdentityCoherence,
        overridePath: opts.PromptPath);
builder.Services.AddSingleton(promptLoader);

builder.Services.AddSingleton(_ => new AnthropicClient(apiKey));
builder.Services.AddSingleton<IChatClient>(sp => sp.GetRequiredService<AnthropicClient>().Messages);

builder.Services.AddSingleton<IdentityCoherenceValidator>();

using var host = builder.Build();

// ============================================================================
// Resolve repo-relative paths
// ============================================================================

var repoRoot = ResolveRepoRoot();
var datasetRoot = Path.Combine(repoRoot, "evals", "dataset");
var tuningRunsRoot = Path.Combine(repoRoot, "evals", "tuning-runs");
if (!Directory.Exists(datasetRoot))
    throw new DirectoryNotFoundException(
        $"Dataset not found at {datasetRoot}. Run from the repo root after regenerating packets.");

// ============================================================================
// Resolve prompt bytes + SHA — even for the embedded path, so the iteration
// log records the exact prompt that produced the metrics.
// ============================================================================

byte[] promptBytes;
string promptDisplayPath;
if (opts.PromptPath is null)
{
    promptBytes = await embeddedLoader.LoadBytesAsync(
        PromptKeys.IdentityCoherence, CancellationToken.None);
    promptDisplayPath = $"embedded:{PromptKeys.IdentityCoherence}";
}
else
{
    promptBytes = await File.ReadAllBytesAsync(opts.PromptPath);
    promptDisplayPath = Path.GetRelativePath(repoRoot, opts.PromptPath);
}
var promptSha = Convert.ToHexStringLower(SHA256.HashData(promptBytes));

// ============================================================================
// Held-out enforcement. The held-out IDs are hard-coded here for portability
// (no Python import); evals/runners/runners/tuning_subsets.py is the source
// of truth — re-pin via a fresh draw with seed=9999 when the canonical
// 50-packet set changes. Cross-checked by Sanity.Heldout below.
// ============================================================================

var packets = opts.Packets ?? Sanity.DefaultTuningSubset;
var heldoutIntersect = packets.Intersect(Sanity.Heldout).ToList();
if (heldoutIntersect.Count > 0 && !opts.AllowHeldout)
    throw new InvalidOperationException(
        $"Refusing to run on held-out packet(s): {string.Join(", ", heldoutIntersect)}. " +
        "Pass --allow-heldout for final-validation runs only.");

// ============================================================================
// Per-packet run loop. One call to the validator per packet per run; the
// --runs flag repeats and takes the WORST-CASE metrics across runs (Sonnet
// at T=0 has residual noise that single-run measurements catch as flicker).
// ============================================================================

var validator = host.Services.GetRequiredService<IdentityCoherenceValidator>();
var emptyProvenance = (IReadOnlyDictionary<string, FieldProvenance>)
    new Dictionary<string, FieldProvenance>();

PacketRunResult[]? worstRunResults = null;
RunMetrics? worstRunMetrics = null;

for (var run = 1; run <= opts.Runs; run++)
{
    Console.WriteLine($"--- Run {run}/{opts.Runs} ---");
    var runResults = new List<PacketRunResult>(packets.Count);
    foreach (var packetId in packets)
    {
        var dir = Path.Combine(datasetRoot, packetId);
        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException(
                $"Packet directory not found: {dir}. Did the dataset regen miss this id?");

        var profile = PacketGoldenLoader.LoadProfile(dir);
        var planted = PacketGoldenLoader.LoadPlantedConflicts(dir);

        var sw = Stopwatch.StartNew();
        var issues = await validator.RunAsync(profile, emptyProvenance, CancellationToken.None);
        sw.Stop();

        runResults.Add(new PacketRunResult(
            packetId, planted, issues, sw.ElapsedMilliseconds));
        Console.WriteLine(
            $"  {packetId,-50} planted={planted.Count} emitted={issues.Count} ({sw.ElapsedMilliseconds}ms)");
        foreach (var i in issues)
            Console.WriteLine($"      → [{i.Severity}] {i.Message}");
    }

    var runMetrics = ComputeMetrics(runResults);
    Console.WriteLine(
        $"  Run {run} metrics: FP={runMetrics.FpRate:P1} ({runMetrics.FpOnCleanCount}+{runMetrics.FpOnDontFlagCount}), " +
        $"recall={runMetrics.Recall:P1} ({runMetrics.ShouldFlagPacketCount - runMetrics.MissedShouldFlagCount}/{runMetrics.ShouldFlagPacketCount})");

    if (worstRunMetrics is null || IsWorse(runMetrics, worstRunMetrics))
    {
        worstRunResults = runResults.ToArray();
        worstRunMetrics = runMetrics;
    }
}

var finalResults = worstRunResults!;
var finalMetrics = worstRunMetrics!;

// ============================================================================
// Aggregate tokens + estimated cost (claude-sonnet-4-6 list pricing as of
// 2026-Q2 per Anthropic public pricing). Cost is "estimated" because cached
// reads aren't separated in M.E.AI's Usage; treat as a ceiling.
// ============================================================================

const double InputCostPerMillion = 3.00;   // USD per million input tokens
const double OutputCostPerMillion = 15.00; // USD per million output tokens
var totalIn = finalResults.Sum(r => r.Issues.Sum(_ => 0L));  // M.E.AI doesn't surface per-call usage on this path
var totalOut = 0L;
// NOTE: token usage on the IChatClient response isn't easy to thread through
// here without modifying IdentityCoherenceValidator's return shape. For task
// 9's iteration loop the wall-time + planted/emitted counts are the load-bearing
// numbers; token-accurate cost lands when ChatResponse.Usage is plumbed out.
var estimatedCost = (totalIn / 1_000_000.0 * InputCostPerMillion)
                  + (totalOut / 1_000_000.0 * OutputCostPerMillion);

// ============================================================================
// Write iteration log + failures TSV
// ============================================================================

var log = new IterationLog(
    Iteration: opts.Iteration,
    RecordedAt: DateTimeOffset.UtcNow,
    PromptPath: promptDisplayPath,
    PromptSha256: promptSha,
    ModelId: IdentityCoherenceValidator.ModelId,
    Notes: opts.Notes,
    PacketIds: packets,
    PacketResults: finalResults.Select(r => new PacketResultLog(
        PacketId: r.PacketId,
        PlantedKinds: r.Planted.Select(p => p.Kind).ToList(),
        PlantedShape: r.Planted.FirstOrDefault(p => p.Shape is not null)?.Shape,
        ExpectedToFlag: r.Planted.Any(p => p.ExpectedToFlag),
        EmittedIssues: r.Issues.Select(i => new EmittedIssueLog(
            Severity: i.Severity.ToString(),
            Message: i.Message,
            Remediation: i.Remediation,
            Sources: i.Citations.Select(c => c.ExtractedValue).ToList())).ToList(),
        InputTokens: 0,
        OutputTokens: 0,
        WallTimeMs: r.WallTimeMs)).ToList(),
    Metrics: finalMetrics,
    TotalInputTokens: totalIn,
    TotalOutputTokens: totalOut,
    EstimatedCostUsd: estimatedCost);

var logPath = IterationLogWriter.Write(log, tuningRunsRoot, baseline: opts.Baseline);
Console.WriteLine($"\nWrote iteration log: {Path.GetRelativePath(repoRoot, logPath)}");

var failures = BuildFailureRows(finalResults);
var tsvPath = FailuresTsv.Write(failures, tuningRunsRoot, opts.Iteration);
Console.WriteLine($"Wrote failures TSV:  {Path.GetRelativePath(repoRoot, tsvPath)} ({failures.Count} rows — fill in `category` column before next iteration)");

// ============================================================================
// Summary
// ============================================================================

Console.WriteLine();
Console.WriteLine("=== Worst-of-runs metrics (used for gate check) ===");
Console.WriteLine($"  FP rate:         {finalMetrics.FpRate:P1} ({finalMetrics.FpOnCleanCount} on clean + {finalMetrics.FpOnDontFlagCount} on don't-flag)");
Console.WriteLine($"  Recall:          {finalMetrics.Recall:P1} ({finalMetrics.ShouldFlagPacketCount - finalMetrics.MissedShouldFlagCount}/{finalMetrics.ShouldFlagPacketCount} should-flag caught)");
Console.WriteLine($"  Clean packets:   {finalMetrics.CleanPacketCount}");
Console.WriteLine($"  Should-flag:     {finalMetrics.ShouldFlagPacketCount}");
Console.WriteLine($"  Don't-flag:      {finalMetrics.DontFlagPacketCount}");
foreach (var (shape, count) in finalMetrics.PerShape.OrderBy(kv => kv.Key))
    Console.WriteLine($"    {shape,-22} planted={count.Planted} flagged={count.Flagged}");
return 0;

// ============================================================================
// Helpers
// ============================================================================

static string ResolveRepoRoot()
{
    // Walk up from cwd looking for a marker. `apps/api` is unique to this repo.
    var dir = Directory.GetCurrentDirectory();
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir, "apps", "api"))
            && Directory.Exists(Path.Combine(dir, "evals", "dataset")))
            return dir;
        var parent = Path.GetDirectoryName(dir);
        if (parent == dir) break;
        dir = parent;
    }
    throw new InvalidOperationException(
        "Couldn't locate repo root (looking for apps/api + evals/dataset). " +
        "Run the CLI from inside the PacketReady repo.");
}

static RunMetrics ComputeMetrics(IReadOnlyList<PacketRunResult> results)
{
    var fpOnClean = 0;
    var fpOnDontFlag = 0;
    var missedShouldFlag = 0;
    var clean = 0;
    var shouldFlag = 0;
    var dontFlag = 0;
    var perShape = new Dictionary<string, (int planted, int flagged)>();

    foreach (var r in results)
    {
        var emitted = r.Issues.Count > 0;
        if (r.Planted.Count == 0)
        {
            clean++;
            if (emitted) fpOnClean++;
        }
        else
        {
            // Treat the packet as should-flag iff ANY planted marker is expected_to_flag.
            // Treat as dont-flag iff ALL planted markers are NOT expected_to_flag.
            var anyShouldFlag = r.Planted.Any(p => p.ExpectedToFlag);
            if (anyShouldFlag)
            {
                shouldFlag++;
                if (!emitted) missedShouldFlag++;
            }
            else
            {
                dontFlag++;
                if (emitted) fpOnDontFlag++;
            }

            foreach (var p in r.Planted)
            {
                if (p.Shape is null) continue;
                var prev = perShape.GetValueOrDefault(p.Shape);
                perShape[p.Shape] = (prev.planted + 1, prev.flagged + (emitted ? 1 : 0));
            }
        }
    }

    var fpDenom = clean + dontFlag;
    var fpRate = fpDenom == 0 ? 0 : (double)(fpOnClean + fpOnDontFlag) / fpDenom;
    var recall = shouldFlag == 0 ? 0 : (double)(shouldFlag - missedShouldFlag) / shouldFlag;

    return new RunMetrics(
        FpRate: fpRate,
        Recall: recall,
        FpOnCleanCount: fpOnClean,
        FpOnDontFlagCount: fpOnDontFlag,
        MissedShouldFlagCount: missedShouldFlag,
        CleanPacketCount: clean,
        ShouldFlagPacketCount: shouldFlag,
        DontFlagPacketCount: dontFlag,
        PerShape: perShape.ToDictionary(
            kv => kv.Key,
            kv => new PerShapeCount(kv.Value.planted, kv.Value.flagged)));
}

static bool IsWorse(RunMetrics candidate, RunMetrics incumbent)
{
    // Worst-of-runs: maximize FP and minimize recall.
    if (candidate.FpRate > incumbent.FpRate) return true;
    if (candidate.FpRate < incumbent.FpRate) return false;
    return candidate.Recall < incumbent.Recall;
}

static List<FailureRow> BuildFailureRows(IReadOnlyList<PacketRunResult> results)
{
    var rows = new List<FailureRow>();
    foreach (var r in results)
    {
        var emitted = r.Issues.Count > 0;
        var firstMessage = r.Issues.FirstOrDefault()?.Message;
        if (r.Planted.Count == 0 && emitted)
            rows.Add(new FailureRow(r.PacketId, "fp_on_clean", firstMessage));
        else if (r.Planted.Count > 0)
        {
            var anyShouldFlag = r.Planted.Any(p => p.ExpectedToFlag);
            if (anyShouldFlag && !emitted)
                rows.Add(new FailureRow(r.PacketId, "missed_should_flag", null));
            else if (!anyShouldFlag && emitted)
                rows.Add(new FailureRow(r.PacketId, "fp_on_dont_flag", firstMessage));
        }
    }
    return rows;
}

internal sealed record PacketRunResult(
    string PacketId,
    IReadOnlyList<PlantedMarker> Planted,
    IReadOnlyList<Issue> Issues,
    long WallTimeMs);

internal sealed class CliOptions
{
    public required int Iteration { get; init; }
    public required string Notes { get; init; }
    public string? PromptPath { get; init; }
    public IReadOnlyList<string>? Packets { get; init; }
    public int Runs { get; init; } = 1;
    public bool Baseline { get; init; }
    public bool AllowHeldout { get; init; }

    public static CliOptions Parse(string[] args)
    {
        int? iteration = null;
        string? notes = null;
        string? promptPath = null;
        IReadOnlyList<string>? packets = null;
        var runs = 1;
        var baseline = false;
        var allowHeldout = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--iteration":  iteration = int.Parse(args[++i]); break;
                case "--notes":      notes = args[++i]; break;
                case "--prompt-path": promptPath = Path.GetFullPath(args[++i]); break;
                case "--packets":
                    packets = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries
                                                 | StringSplitOptions.TrimEntries);
                    break;
                case "--runs":           runs = int.Parse(args[++i]); break;
                case "--baseline":       baseline = true; break;
                case "--allow-heldout":  allowHeldout = true; break;
                case "--help" or "-h":   PrintHelp(); Environment.Exit(0); break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        if (iteration is null)
            throw new ArgumentException("--iteration is required.");
        if (string.IsNullOrWhiteSpace(notes))
            throw new ArgumentException("--notes is required (forces iteration-intent capture).");
        if (runs < 1)
            throw new ArgumentException("--runs must be >= 1.");
        if (baseline && iteration != 0)
            throw new ArgumentException("--baseline requires --iteration 0.");

        return new CliOptions
        {
            Iteration = iteration.Value,
            Notes = notes!,
            PromptPath = promptPath,
            Packets = packets,
            Runs = runs,
            Baseline = baseline,
            AllowHeldout = allowHeldout,
        };
    }

    public void Validate()
    {
        if (PromptPath is not null && !File.Exists(PromptPath))
            throw new FileNotFoundException($"--prompt-path file not found: {PromptPath}");
    }

    static void PrintHelp() => Console.WriteLine("""
        TuneIdentityCoherence — run IdentityCoherenceValidator against fixture packets, log results.

        Required:
          --iteration N        Iteration index (0 = baseline; positive = tuning iterations).
          --notes "..."        What changed this iteration (target category + change type).

        Optional:
          --prompt-path PATH   Override the embedded prompt with a file on disk.
          --packets a,b,c      Packet IDs to run against. Defaults to IDENTITY_COHERENCE_TUNING.
          --runs N             Repeat the run N times; worst-of metrics are reported. Default 1.
          --baseline           Write to baseline.json (refuses if exists). Requires --iteration 0.
          --allow-heldout      Permit IDs from IDENTITY_COHERENCE_HELDOUT. Final-validation only.
          --help               Show this help.

        Examples:
          # Baseline (iteration 0):
          dotnet run --project tools/TuneIdentityCoherence -- \\
            --iteration 0 --baseline --notes "baseline v1 unmodified"

          # Tuning iteration 3, prompt edit:
          dotnet run --project tools/TuneIdentityCoherence -- \\
            --iteration 3 --prompt-path my-edited-prompt.md \\
            --notes "credential_suffix: instruction-level rule for M.D. dotted form"

          # Final validation on held-out:
          dotnet run --project tools/TuneIdentityCoherence -- \\
            --iteration 99 --runs 3 --allow-heldout \\
            --packets <held-out-csv> --notes "final validation held-out"
        """);
}

/// <summary>
/// Hardcoded packet IDs mirrored from
/// <c>evals/runners/runners/tuning_subsets.py</c>. Python is the source of
/// truth; this CLI is the consumer. Re-pin both when the canonical 50-packet
/// set changes. <see cref="PacketReady.TuneIdentityCoherence.Tests"/> (when
/// added) asserts these match the Python tuples.
/// </summary>
internal static class Sanity
{
    public static readonly IReadOnlyList<string> DefaultTuningSubset =
    [
        "packet-010-clean-berry",                       // CREDENTIAL_MD
        "packet-006-clean-perry",                       // CREDENTIAL_PERIODS
        "packet-018-clean-rogers",                      // HYPHENATED_ALREADY
        "packet-007-clean-myers",                       // MIDDLE_INITIAL
        "packet-014-clean-barker",                      // WHITESPACE_VARIANT
        "packet-003-conflict-name",                     // HYPHENATED_SUFFIX
        "packet-025-clean-conflict-name-bartlett",      // MIDDLE_NAME_ADDED
        "packet-021-clean-conflict-name-guzman",        // NICKNAME
        "packet-022-clean-conflict-name-alexander",     // SURNAME_TYPO (don't-flag)
        "packet-023-clean-conflict-name-cummings",      // SURNAME_SWAP
    ];

    public static readonly IReadOnlyList<string> Heldout =
    [
        "packet-005-scanned-anderson",
        "packet-008-clean-lopez",
        "packet-009-clean-cervantes",
        "packet-011-clean-wilson",
        "packet-013-clean-jackson",
        "packet-019-clean-conflict-name-foster",
        "packet-020-clean-conflict-name-parker",
        "packet-038-scanned-ray",
        "packet-039-scanned-king",
        "packet-046-scanned-conflict-name-blanchard",
    ];
}

/// <summary>
/// One-key prompt loader override for <c>--prompt-path</c>. Returns the
/// file's bytes for the override key; delegates everything else to the
/// embedded loader. The CLI never asks for any other prompt, but the
/// fallback keeps the loader contract intact in case a future shared
/// helper does.
/// </summary>
internal sealed class FileSystemOverridePromptLoader : IPromptLoader
{
    private readonly IPromptLoader _inner;
    private readonly string _overrideKey;
    private readonly string _overridePath;
    private byte[]? _cachedBytes;

    public FileSystemOverridePromptLoader(
        IPromptLoader inner, string overrideKey, string overridePath)
    {
        _inner = inner;
        _overrideKey = overrideKey;
        _overridePath = overridePath;
    }

    public async Task<string> LoadAsync(string promptPath, CancellationToken ct)
        => promptPath == _overrideKey
            ? Encoding.UTF8.GetString(await LoadBytesAsync(promptPath, ct))
            : await _inner.LoadAsync(promptPath, ct);

    public async Task<string> LoadAsync(
        string promptPath,
        IReadOnlyDictionary<string, string> variables,
        CancellationToken ct)
    {
        var raw = await LoadAsync(promptPath, ct);
        // Match PromptLoader's simple {{key}} substitution. The
        // IdentityCoherence prompt doesn't currently use variables, but the
        // contract is the contract.
        foreach (var (k, v) in variables)
            raw = raw.Replace("{{" + k + "}}", v);
        return raw;
    }

    public Task<byte[]> LoadBytesAsync(string promptPath, CancellationToken ct)
        => promptPath == _overrideKey
            ? Task.FromResult(_cachedBytes ??= File.ReadAllBytes(_overridePath))
            : _inner.LoadBytesAsync(promptPath, ct);
}
