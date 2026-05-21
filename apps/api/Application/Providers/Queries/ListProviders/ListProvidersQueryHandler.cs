using MediatR;
using Microsoft.EntityFrameworkCore;
using PacketReady.Application.Abstractions;
using PacketReady.Domain.Providers;

namespace PacketReady.Application.Providers.Queries.ListProviders;

public sealed class ListProvidersQueryHandler
    : IRequestHandler<ListProvidersQuery, IReadOnlyList<ProviderListItemDto>>
{
    /// <summary>
    /// Defensive cap so a runaway fixture seed can't OOM the response. P1 fixtures
    /// are &lt;10 rows; pagination lands in P4 when this cap actually bites.
    /// </summary>
    private const int MaxRows = 500;

    private readonly IAppDbContext _db;

    public ListProvidersQueryHandler(IAppDbContext db) => _db = db;

    public async Task<IReadOnlyList<ProviderListItemDto>> Handle(
        ListProvidersQuery request, CancellationToken ct)
    {
        // One SQL round-trip: each provider plus a correlated subquery for the latest
        // score. FullName lives inside ProfileJson and is unpacked in memory because
        // EF can't deserialize ProviderProfile from JSONB inside a projection;
        // Phase 4 can switch to Profile->>'FullName' once row counts justify it.
        //
        // Order by CreatedAt ASC so the dashboard list is stable across reloads
        // (no implicit row order from Postgres heap), with Id as final tiebreaker
        // for the unlikely same-tick insert. Score subquery has its own tiebreaker
        // on Id to disambiguate same-tick recomputes.
        var rows = await _db.Providers
            .OrderBy(p => p.CreatedAt)
            .ThenBy(p => p.Id)
            .Take(MaxRows)
            .Select(p => new
            {
                p.Id,
                p.ProfileJson,
                Latest = _db.ReadinessScores
                    .Where(s => s.ProviderId == p.Id)
                    .OrderByDescending(s => s.ComputedAt)
                    .ThenByDescending(s => s.Id)
                    .Select(s => new { s.Score, s.Tier, s.ComputedAt })
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        return rows.Select(r =>
        {
            var profile = ProviderProfile.FromJson(r.ProfileJson, r.Id);
            return new ProviderListItemDto(
                Id: r.Id,
                FullName: profile.FullName,
                LatestScore: r.Latest?.Score,
                LatestTier: r.Latest?.Tier,
                LatestComputedAt: r.Latest?.ComputedAt);
        }).ToList();
    }
}
