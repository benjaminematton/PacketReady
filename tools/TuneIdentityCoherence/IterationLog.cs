using System.Text.Json;
using System.Text.Json.Serialization;

namespace PacketReady.TuneIdentityCoherence;

/// <summary>
/// Per-run audit record committed to <c>evals/tuning-runs/</c>. The set of
/// committed log files IS the iteration history — the regression check
/// "did the targeted category move?" is answered by diffing two files in
/// that directory, not by terminal scrollback.
///
/// <para>Three fields anchor reproducibility: <see cref="PromptSha256"/>
/// pins the exact prompt bytes, <see cref="ModelId"/> pins the Sonnet
/// revision, and <see cref="Iteration"/> orders the runs.</para>
/// </summary>
public sealed record IterationLog(
    [property: JsonPropertyName("iteration")] int Iteration,
    [property: JsonPropertyName("recordedAt")] DateTimeOffset RecordedAt,
    [property: JsonPropertyName("promptPath")] string PromptPath,
    [property: JsonPropertyName("promptSha256")] string PromptSha256,
    [property: JsonPropertyName("modelId")] string ModelId,
    [property: JsonPropertyName("notes")] string Notes,
    [property: JsonPropertyName("packetIds")] IReadOnlyList<string> PacketIds,
    [property: JsonPropertyName("packetResults")] IReadOnlyList<PacketResultLog> PacketResults,
    [property: JsonPropertyName("metrics")] RunMetrics Metrics,
    [property: JsonPropertyName("totalInputTokens")] long TotalInputTokens,
    [property: JsonPropertyName("totalOutputTokens")] long TotalOutputTokens,
    [property: JsonPropertyName("estimatedCostUsd")] double EstimatedCostUsd);

public sealed record PacketResultLog(
    [property: JsonPropertyName("packetId")] string PacketId,
    [property: JsonPropertyName("plantedKinds")] IReadOnlyList<string> PlantedKinds,
    [property: JsonPropertyName("plantedShape")] string? PlantedShape,
    [property: JsonPropertyName("expectedToFlag")] bool ExpectedToFlag,
    [property: JsonPropertyName("emittedIssues")] IReadOnlyList<EmittedIssueLog> EmittedIssues,
    [property: JsonPropertyName("inputTokens")] long InputTokens,
    [property: JsonPropertyName("outputTokens")] long OutputTokens,
    [property: JsonPropertyName("wallTimeMs")] long WallTimeMs);

public sealed record EmittedIssueLog(
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("remediation")] string Remediation,
    [property: JsonPropertyName("sources")] IReadOnlyList<string> Sources);

public sealed record RunMetrics(
    [property: JsonPropertyName("fpRate")] double FpRate,
    [property: JsonPropertyName("recall")] double Recall,
    [property: JsonPropertyName("fpOnCleanCount")] int FpOnCleanCount,
    [property: JsonPropertyName("fpOnDontFlagCount")] int FpOnDontFlagCount,
    [property: JsonPropertyName("missedShouldFlagCount")] int MissedShouldFlagCount,
    [property: JsonPropertyName("cleanPacketCount")] int CleanPacketCount,
    [property: JsonPropertyName("shouldFlagPacketCount")] int ShouldFlagPacketCount,
    [property: JsonPropertyName("dontFlagPacketCount")] int DontFlagPacketCount,
    [property: JsonPropertyName("perShape")] IReadOnlyDictionary<string, PerShapeCount> PerShape);

public sealed record PerShapeCount(
    [property: JsonPropertyName("planted")] int Planted,
    [property: JsonPropertyName("flagged")] int Flagged);


public static class IterationLogWriter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Writes one of two filename shapes:
    /// <list type="bullet">
    ///   <item><c>baseline.json</c> when <paramref name="baseline"/> is true.
    ///   REFUSES to overwrite if the file exists — the floor is committed
    ///   once and not edited.</item>
    ///   <item><c>iter-NN__YYYY-MM-DDTHHMMSSZ__&lt;sha-prefix&gt;.json</c>
    ///   for every other run. Multiple runs at the same iteration are fine;
    ///   the timestamp disambiguates.</item>
    /// </list>
    /// Returns the absolute path written.
    /// </summary>
    public static string Write(
        IterationLog log,
        string tuningRunsRoot,
        bool baseline)
    {
        Directory.CreateDirectory(tuningRunsRoot);

        string path;
        if (baseline)
        {
            path = Path.Combine(tuningRunsRoot, "baseline.json");
            if (File.Exists(path))
                throw new InvalidOperationException(
                    $"Refusing to overwrite {path}. The baseline is the v1 floor — " +
                    "delete the file explicitly if you genuinely want to re-establish it.");
        }
        else
        {
            var iter = log.Iteration.ToString("00");
            var iso = log.RecordedAt.ToString("yyyy-MM-ddTHHmmssZ");
            var sha = log.PromptSha256[..Math.Min(8, log.PromptSha256.Length)];
            path = Path.Combine(tuningRunsRoot, $"iter-{iter}__{iso}__{sha}.json");
        }

        File.WriteAllText(path, JsonSerializer.Serialize(log, JsonOpts) + "\n");
        return path;
    }
}
