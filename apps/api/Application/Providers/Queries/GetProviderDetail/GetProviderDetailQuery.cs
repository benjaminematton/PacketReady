using MediatR;
using PacketReady.Application.Scoring.Commands.ComputeReadinessScore;

namespace PacketReady.Application.Providers.Queries.GetProviderDetail;

/// <summary>
/// Returns a provider summary plus their latest readiness score (if any).
/// Drives the dashboard's drill-in detail view. Returns <see langword="null"/> when
/// the provider doesn't exist — endpoint maps that to 404.
/// </summary>
public sealed record GetProviderDetailQuery(Guid ProviderId) : IRequest<ProviderDetailDto?>;

/// <summary>
/// Detail-view projection. Profile fields are surfaced selectively (name, NPI,
/// credentialing state) rather than passing the whole <c>ProviderProfile</c> —
/// keeps the wire shape stable when we add fields to the domain record in P3+.
/// </summary>
public sealed record ProviderDetailDto(
    Guid Id,
    string FullName,
    string Npi,
    string CredentialingState,
    DateTimeOffset CreatedAt,
    ReadinessScoreDto? LatestScore);
