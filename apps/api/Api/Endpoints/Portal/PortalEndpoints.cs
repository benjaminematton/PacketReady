using Microsoft.EntityFrameworkCore;
using PacketReady.Application.Abstractions;
using PacketReady.Application.Intake.MagicLinks;
using PacketReady.Domain.Intake;
using PacketReady.Domain.MagicLinks;
using PacketReady.Domain.Providers;

namespace PacketReady.Api.Endpoints.Portal;

/// <summary>
/// Provider-facing portal endpoints. Token rides in the URL path
/// (<c>/api/portal/{token}</c>) per phase-5-intake-agent.md task 6;
/// validation lives inline here rather than an ASP.NET auth scheme
/// because the token IS the URL — no Authorization header gymnastics
/// needed.
///
/// <para><b>C3 surface (minimal).</b> GET returns a small portal state
/// (provider name + session status + link expiry) the Next.js page can
/// render the "you're in the right place" screen against. POST consumes
/// the link, transitions the session, and acks. The full extraction-cards
/// UX from <c>design.md §7.9</c> (per-field confirm/edit) is C4+.</para>
/// </summary>
public static class PortalEndpoints
{
    public sealed record PortalStateDto(
        Guid ProviderId,
        string? ProviderFullName,
        Guid IntakeSessionId,
        IntakeState SessionState,
        DateTimeOffset LinkIssuedAt,
        DateTimeOffset LinkExpiresAt);

    public sealed record PortalSubmitRequest(
        // C3 ships an empty submit body. C4 expands this with confirmed /
        // edited extraction fields per design.md §7.9. Keeping the shape
        // here so the API contract is callable end-to-end today; clients
        // can pass {} and get a 200.
        Dictionary<string, object?>? ConfirmedFields = null);

    public static IEndpointRouteBuilder MapPortalEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/portal/{token}",
            async (string token,
                IMagicLinkAuthority authority,
                IAppDbContext db,
                TimeProvider clock,
                CancellationToken ct) =>
        {
            MagicLink link;
            try
            {
                link = await authority.ValidateAsync(token, clock.GetUtcNow(), ct);
            }
            catch (MagicLinkInvalidException ex)
            {
                return ProblemResults.MagicLinkInvalid(ex.Reason.ToString());
            }

            var session = await db.IntakeSessions
                .AsNoTracking()
                .SingleOrDefaultAsync(s => s.ProviderId == link.ProviderId, ct);
            if (session is null)
                return ProblemResults.MagicLinkInvalid(
                    MagicLinkInvalidReason.NotFound.ToString());

            // Provider lookup is best-effort: a missing profile JSON is
            // not a portal error — the page can render "Welcome" without
            // a name. FullName comes from ProviderProfile if present.
            var provider = await db.Providers
                .AsNoTracking()
                .SingleOrDefaultAsync(p => p.Id == link.ProviderId, ct);

            return Results.Ok(new PortalStateDto(
                ProviderId: link.ProviderId,
                ProviderFullName: TryExtractFullName(provider),
                IntakeSessionId: session.Id,
                SessionState: session.State,
                LinkIssuedAt: link.IssuedAt,
                LinkExpiresAt: link.ExpiresAt));
        })
            .WithName("PortalGet")
            .WithTags("Portal")
            .Produces<PortalStateDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status410Gone);

        app.MapPost("/api/portal/{token}/submit",
            async (string token,
                PortalSubmitRequest? body,
                IMagicLinkAuthority authority,
                IAppDbContext db,
                TimeProvider clock,
                CancellationToken ct) =>
        {
            var now = clock.GetUtcNow();

            MagicLink link;
            try
            {
                link = await authority.ValidateAsync(token, now, ct);
            }
            catch (MagicLinkInvalidException ex)
            {
                return ProblemResults.MagicLinkInvalid(ex.Reason.ToString());
            }

            // Single-use consume. The DB-side guarantee against a concurrent
            // double-click is a SELECT FOR UPDATE inside the same
            // transaction (deferred hardening — phase-5-intake-agent.md
            // "Magic-link replay" risk). For C3 we rely on the aggregate's
            // in-memory refusal plus SaveChanges' write-ordering: a second
            // submit re-loads from the DB and sees consumed_at already
            // stamped.
            link.Consume(now);
            await db.SaveChangesAsync(ct);

            // C3 doesn't process the body — the confirmed-fields path lands
            // in C4 alongside the agent runtime. Accepting the body shape
            // here lets the portal page wire the POST today without a
            // second round-trip when C4 ships.
            _ = body;

            return Results.Ok(new
            {
                providerId = link.ProviderId,
                magicLinkId = link.Id,
                consumedAt = link.ConsumedAt,
            });
        })
            .WithName("PortalSubmit")
            .WithTags("Portal")
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status410Gone);

        return app;
    }

    // Best-effort name pluck. Provider.GetProfile() throws on invalid JSON;
    // empty "{}" returns a ProviderProfile with all-default fields. We
    // shield the endpoint from both — a missing name is just absent in the
    // response, not a 500.
    private static string? TryExtractFullName(Provider? provider)
    {
        if (provider is null) return null;
        try
        {
            var profile = provider.GetProfile();
            return string.IsNullOrWhiteSpace(profile.FullName) ? null : profile.FullName;
        }
        catch
        {
            return null;
        }
    }
}
