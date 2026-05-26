using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using PacketReady.Application.Abstractions;
using PacketReady.Application.Intake.MagicLinks;
using PacketReady.Domain.Documents;
using PacketReady.Domain.Intake;
using PacketReady.Domain.MagicLinks;
using PacketReady.Domain.Providers;
using PacketReady.Infrastructure.Intake;

namespace PacketReady.Api.Endpoints.Portal;

/// <summary>
/// Provider-facing portal endpoints. Token rides in the URL path
/// (<c>/api/portal/{token}</c>) per phase-5-intake-agent.md task 6;
/// validation lives inline here rather than an ASP.NET auth scheme
/// because the token IS the URL — no Authorization header gymnastics
/// needed.
///
/// <para><b>GET surface.</b> Returns provider name + session status +
/// link expiry + the per-document extraction state §7.9 calls for
/// (each uploaded doc's latest succeeded extraction, with field
/// values, locations, and per-field confidence). The Next.js portal
/// renders these as cards so the provider sees what we pulled before
/// submitting. Per-field <i>edit</i> (the §7.9 append-a-new-extraction
/// path with <c>source='provider_edit'</c>) is still deferred — the
/// POST body's <c>ConfirmedFields</c> shape ignores edits for now.</para>
///
/// <para><b>POST surface.</b> Consumes the link, transitions the
/// session, enqueues the agent turn. Lossless in the sense that a
/// failed Hangfire enqueue surfaces as 500 (the consume is already
/// committed; we never return 200 with no job queued).</para>
/// </summary>
public static class PortalEndpoints
{
    public sealed record PortalStateDto(
        Guid ProviderId,
        string? ProviderFullName,
        Guid IntakeSessionId,
        IntakeState SessionState,
        DateTimeOffset LinkIssuedAt,
        DateTimeOffset LinkExpiresAt,
        IReadOnlyList<PortalDocumentDto> Documents);

    /// <summary>
    /// One uploaded document's view for the portal. <see cref="LatestExtraction"/>
    /// is null when the classifier saw the file but the extractor failed
    /// or hasn't run yet — surfaces as an empty card so the provider
    /// sees "we have your file but haven't read it yet" rather than the
    /// doc silently dropping from the page.
    /// </summary>
    public sealed record PortalDocumentDto(
        Guid DocumentId,
        string DocType,
        double? DocTypeConfidence,
        string OriginalName,
        int PageCount,
        DateTimeOffset UploadedAt,
        PortalExtractionDto? LatestExtraction);

    /// <summary>
    /// JSONB blobs travel as raw JSON strings so the wire format
    /// matches what's stored. The Next.js page parses each and renders
    /// one row per field — value + bbox / page (from
    /// <see cref="FieldLocationsJson"/>) + 0..1 confidence (from
    /// <see cref="ConfidenceJson"/>).
    /// </summary>
    public sealed record PortalExtractionDto(
        Guid ExtractionId,
        string SchemaVersion,
        string FieldsJson,
        string FieldLocationsJson,
        string ConfidenceJson,
        DateTimeOffset ExtractedAt,
        DateTimeOffset? ConfirmedAt);

    public sealed record PortalSubmitRequest(
        // C3 ships an empty submit body. C4 expands this with confirmed /
        // edited extraction fields per design.md §7.9. Keeping the shape
        // here so the API contract is callable end-to-end today; clients
        // can pass {} and get a 200. Typed as JsonElement? (not
        // Dictionary<string, object?>) so STJ doesn't lower numbers to
        // JsonElement-inside-object — C4 walks the raw element directly.
        JsonElement? ConfirmedFields = null);

    /// <summary>Stable category for portal endpoint logging.</summary>
    private const string LoggerCategory = "PacketReady.Api.Endpoints.Portal";

    public static IEndpointRouteBuilder MapPortalEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/portal/{token}",
            async (string token,
                IMagicLinkAuthority authority,
                IAppDbContext db,
                TimeProvider clock,
                ILoggerFactory loggerFactory,
                CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger(LoggerCategory);

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
            {
                // System invariant violation: StartIntake atomically creates
                // session + link in one transaction, so a valid link with no
                // session means data corruption (manual DELETE, bad
                // backfill). Surface as 500 — masking it as a user-facing
                // 410 would hide the bug.
                logger.LogError(
                    "Portal invariant broken: magic_link {LinkId} for provider {ProviderId} has no intake_sessions row.",
                    link.Id, link.ProviderId);
                throw new InvalidOperationException(
                    $"No intake_sessions row for provider {link.ProviderId} despite a valid magic link.");
            }

            // Provider lookup is best-effort: a missing profile JSON is
            // not a portal error — the page can render "Welcome" without
            // a name. FullName comes from ProviderProfile if present.
            var provider = await db.Providers
                .AsNoTracking()
                .SingleOrDefaultAsync(p => p.Id == link.ProviderId, ct);

            // Pull every uploaded document for this provider, joined to
            // its latest succeeded extraction (one row per
            // document_id × MAX(extracted_at)). EF translates the
            // correlated subquery to a LATERAL on Postgres; the
            // resulting plan reads ix_document_extractions_doc_schema_extracted
            // index-only. AsNoTracking — strictly a read.
            var documents = await db.Documents
                .AsNoTracking()
                .Where(d => d.ProviderId == link.ProviderId)
                .OrderBy(d => d.UploadedAt)
                .Select(d => new
                {
                    Document = d,
                    LatestExtraction = db.DocumentExtractions
                        .AsNoTracking()
                        .Where(e => e.DocumentId == d.Id
                                 && e.Status == ExtractionStatus.Succeeded)
                        .OrderByDescending(e => e.ExtractedAt)
                        .FirstOrDefault(),
                })
                .ToListAsync(ct);

            var documentDtos = documents
                .Select(row => new PortalDocumentDto(
                    DocumentId: row.Document.Id,
                    DocType: row.Document.DocType?.ToString() ?? "Unknown",
                    DocTypeConfidence: row.Document.DocTypeConfidence,
                    OriginalName: row.Document.OriginalName,
                    PageCount: row.Document.PageCount,
                    UploadedAt: row.Document.UploadedAt,
                    LatestExtraction: row.LatestExtraction is null ? null : new PortalExtractionDto(
                        ExtractionId: row.LatestExtraction.Id,
                        SchemaVersion: row.LatestExtraction.SchemaVersion,
                        FieldsJson: row.LatestExtraction.FieldsJson,
                        FieldLocationsJson: row.LatestExtraction.FieldLocationsJson,
                        ConfidenceJson: row.LatestExtraction.ConfidenceJson,
                        ExtractedAt: row.LatestExtraction.ExtractedAt,
                        ConfirmedAt: row.LatestExtraction.ConfirmedAt)))
                .ToList();

            return Results.Ok(new PortalStateDto(
                ProviderId: link.ProviderId,
                ProviderFullName: TryExtractFullName(provider, logger),
                IntakeSessionId: session.Id,
                SessionState: session.State,
                LinkIssuedAt: link.IssuedAt,
                LinkExpiresAt: link.ExpiresAt,
                Documents: documentDtos));
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
                IBackgroundJobClient jobs,
                TimeProvider clock,
                ILoggerFactory loggerFactory,
                CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger(LoggerCategory);
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

            // Single-use consume. Two layers guard the replay:
            //   1. MagicLink.Consume refuses double-consume in-memory.
            //   2. consumed_at is an EF concurrency token: a concurrent
            //      submit from a sibling DbContext finds zero rows
            //      affected on UPDATE and raises DbUpdateConcurrencyException.
            // The first layer catches sequential same-context replays;
            // the second catches genuinely concurrent requests.
            link.Consume(now);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // The loser of a concurrent consume race. The winner already
                // stamped consumed_at; presenting this as Consumed (not a
                // 5xx) matches the user-visible truth.
                logger.LogInformation(
                    "Portal submit lost the consume race for magic_link {LinkId}; surfacing as Consumed.",
                    link.Id);
                return ProblemResults.MagicLinkInvalid(
                    MagicLinkInvalidReason.Consumed.ToString());
            }

            // C3 doesn't process the body — the confirmed-fields path lands
            // in C4 alongside the agent runtime. Accepting the body shape
            // here lets the portal page wire the POST today without a
            // second round-trip when C4 ships.
            _ = body;

            // C5: enqueue the agent turn. The consume above is already
            // committed, so a failed enqueue leaves the session in a
            // half-progressed state (link consumed, no AgentProcessing
            // turn). Surface that as a 500 so an admin can replay via
            // the Hangfire dashboard or a future watchdog; never lose the
            // signal by returning 200 with no job queued.
            string turnJobId;
            try
            {
                turnJobId = jobs.Enqueue<IntakeTurnJob>(j =>
                    j.RunAsync(link.ProviderId, CancellationToken.None));
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Hangfire enqueue failed for provider {ProviderId} after magic_link {LinkId} was consumed; manual re-enqueue required.",
                    link.ProviderId, link.Id);
                return ProblemResults.PortalEnqueueFailed(link.ProviderId);
            }

            return Results.Ok(new
            {
                providerId = link.ProviderId,
                magicLinkId = link.Id,
                consumedAt = link.ConsumedAt,
                turnJobId,
            });
        })
            .WithName("PortalSubmit")
            .WithTags("Portal")
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status410Gone);

        return app;
    }

    // Best-effort name pluck. Provider.GetProfile() throws on malformed
    // JSON; empty "{}" returns a ProviderProfile with all-default fields.
    // Both fall back to null on the wire (the page can render "Welcome"
    // without a name) but malformed JSON is genuine data corruption — log
    // a warning so we notice instead of silently returning null forever.
    private static string? TryExtractFullName(Provider? provider, ILogger logger)
    {
        if (provider is null) return null;
        try
        {
            var profile = provider.GetProfile();
            return string.IsNullOrWhiteSpace(profile.FullName) ? null : profile.FullName;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Provider {ProviderId} profile JSON did not parse; portal rendering without a name.",
                provider.Id);
            return null;
        }
    }
}
