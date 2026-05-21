using MediatR;
using PacketReady.Domain.Scoring;

namespace PacketReady.Application.Scoring.Commands.ComputeReadinessScore;

/// <summary>
/// Recomputes the readiness score for a provider. Always writes a new
/// <see cref="ReadinessScore"/> row — never updates an existing one — so the
/// audit trail preserves history when rubrics or extractions change.
/// </summary>
public sealed record ComputeReadinessScoreCommand(Guid ProviderId) : IRequest<ReadinessScoreDto>;

/// <summary>
/// Wire-format projection of <see cref="ReadinessScore"/>. Exposes <see cref="Issues"/>
/// inline so the dashboard's side-panel doesn't have to round-trip the JSONB blob.
/// </summary>
public sealed record ReadinessScoreDto(
    Guid Id,
    Guid ProviderId,
    int Score,
    Tier Tier,
    int CriticalCount,
    int MajorCount,
    int MinorCount,
    IReadOnlyList<Issue> Issues,
    DateTimeOffset ComputedAt)
{
    /// <summary>
    /// Projects a persisted <see cref="ReadinessScore"/> to wire shape, deserializing
    /// <c>IssuesJson</c> once. Single source of truth for read handlers and the
    /// score-compute command.
    /// </summary>
    public static ReadinessScoreDto From(ReadinessScore score) =>
        new(
            Id: score.Id,
            ProviderId: score.ProviderId,
            Score: score.Score,
            Tier: score.Tier,
            CriticalCount: score.CriticalCount,
            MajorCount: score.MajorCount,
            MinorCount: score.MinorCount,
            Issues: score.GetIssues(),
            ComputedAt: score.ComputedAt);
}

/// <summary>
/// Thrown when <see cref="ComputeReadinessScoreCommand.ProviderId"/> does not match
/// any provider row. The API layer catches this and maps to a 404 — handlers stay
/// HTTP-agnostic.
/// </summary>
public sealed class ProviderNotFoundException(Guid providerId)
    : Exception($"Provider {providerId} not found.")
{
    public Guid ProviderId { get; } = providerId;
}
