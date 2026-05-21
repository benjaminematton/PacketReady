using MediatR;
using Microsoft.EntityFrameworkCore;
using PacketReady.Application.Abstractions;
using PacketReady.Application.Scoring.Commands.ComputeReadinessScore;
using PacketReady.Domain.Providers;

namespace PacketReady.Application.Providers.Queries.GetProviderDetail;

public sealed class GetProviderDetailQueryHandler
    : IRequestHandler<GetProviderDetailQuery, ProviderDetailDto?>
{
    private readonly IAppDbContext _db;

    public GetProviderDetailQueryHandler(IAppDbContext db) => _db = db;

    public async Task<ProviderDetailDto?> Handle(GetProviderDetailQuery request, CancellationToken ct)
    {
        // Same shape as ListProvidersQueryHandler — pull the provider plus latest
        // score in one round-trip, deserialize the profile in memory. Detail differs
        // in carrying the full latest-score row (with Issues) for the side-panel.
        // Score subquery has an Id tiebreaker to disambiguate same-tick recomputes.
        var row = await _db.Providers
            .Where(p => p.Id == request.ProviderId)
            .Select(p => new
            {
                p.Id,
                p.CreatedAt,
                p.ProfileJson,
                Latest = _db.ReadinessScores
                    .Where(s => s.ProviderId == p.Id)
                    .OrderByDescending(s => s.ComputedAt)
                    .ThenByDescending(s => s.Id)
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);

        if (row is null) return null;

        var profile = ProviderProfile.FromJson(row.ProfileJson, row.Id);

        return new ProviderDetailDto(
            Id: row.Id,
            FullName: profile.FullName,
            Npi: profile.Npi,
            CredentialingState: profile.CredentialingState,
            CreatedAt: row.CreatedAt,
            LatestScore: row.Latest is null ? null : ReadinessScoreDto.From(row.Latest));
    }
}
