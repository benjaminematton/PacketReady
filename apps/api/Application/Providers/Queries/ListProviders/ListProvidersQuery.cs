using MediatR;
using PacketReady.Domain.Scoring;

namespace PacketReady.Application.Providers.Queries.ListProviders;

/// <summary>
/// Lists all providers with their latest readiness score (if any). Drives the
/// dashboard's provider list view. No pagination in P1 — fixture sets are &lt; 10 rows.
/// </summary>
public sealed record ListProvidersQuery() : IRequest<IReadOnlyList<ProviderListItemDto>>;

/// <summary>
/// One row in the provider list. Score fields are nullable because a provider may
/// exist without yet having a score computed (the fixture seed creates the row, then
/// a separate <c>POST /scores</c> call generates the first score).
/// </summary>
public sealed record ProviderListItemDto(
    Guid Id,
    string FullName,
    int? LatestScore,
    Tier? LatestTier,
    DateTimeOffset? LatestComputedAt);
