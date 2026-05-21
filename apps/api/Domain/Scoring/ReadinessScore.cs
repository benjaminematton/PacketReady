using System.Text.Json;

namespace PacketReady.Domain.Scoring;

/// <summary>
/// One score computation for a provider. Append-only by convention — when the rubric
/// changes or extractions are refreshed, a new row lands rather than updating the old
/// one. No DB trigger enforces this in Phase 1 (unlike <c>audit_events</c>); revisit
/// in P4 if rubric churn becomes a concern.
/// </summary>
public class ReadinessScore
{
    public Guid Id { get; private set; }
    public Guid ProviderId { get; private set; }
    public int Score { get; private set; }
    public Tier Tier { get; private set; }
    public int CriticalCount { get; private set; }
    public int MajorCount { get; private set; }
    public int MinorCount { get; private set; }

    /// <summary>
    /// JSONB column; serialized <c>IReadOnlyList&lt;Issue&gt;</c> using
    /// <see cref="DomainJson.Options"/>. Default <c>"[]"</c> exists for EF materialization.
    /// </summary>
    public string IssuesJson { get; private set; } = "[]";

    public DateTimeOffset ComputedAt { get; private set; }

    private ReadinessScore() { }

    /// <summary>
    /// Constructs a score from raw inputs. <see cref="Tier"/> is derived from
    /// <paramref name="score"/>; severity counts are derived from <paramref name="issues"/>.
    /// Callers cannot smuggle in a contradictory tier or count.
    /// </summary>
    public static ReadinessScore Create(
        Guid providerId,
        int score,
        IReadOnlyList<Issue> issues,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(issues);
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
            IssuesJson = JsonSerializer.Serialize(issues, DomainJson.Options),
            ComputedAt = now,
        };
    }

    public IReadOnlyList<Issue> GetIssues() =>
        JsonSerializer.Deserialize<List<Issue>>(IssuesJson, DomainJson.Options)
        ?? throw new InvalidOperationException(
            $"ReadinessScore {Id} has invalid issues JSON; cannot deserialize.");
}
