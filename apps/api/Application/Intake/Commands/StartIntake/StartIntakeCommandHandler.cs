using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PacketReady.Application.Abstractions;
using PacketReady.Application.Audit;
using PacketReady.Application.Intake.Audit;
using PacketReady.Application.Intake.Exceptions;
using PacketReady.Application.Intake.MagicLinks;
using PacketReady.Application.Providers.Exceptions;
using PacketReady.Domain.Audit;
using PacketReady.Domain.Intake;
using PacketReady.Domain.MagicLinks;
using PacketReady.Domain.Messaging;

namespace PacketReady.Application.Intake.Commands.StartIntake;

public sealed class StartIntakeCommandHandler : IRequestHandler<StartIntakeCommand, StartIntakeResult>
{
    // Pinned by constraint name so a future UNIQUE added to intake_sessions
    // (e.g. on a derived column) does NOT silently get treated as "already
    // started". Mirrors the ExtractionPersister.IsIdempotencyRaceLost shape.
    private const string IntakeProviderUniqueConstraint = "ux_intake_sessions_provider";

    private readonly IAppDbContext _db;
    private readonly IMagicLinkAuthority _authority;
    private readonly IAuditWriter _audit;
    private readonly IDbExceptionTranslator _dbExceptionTranslator;
    private readonly TimeProvider _clock;
    private readonly ILogger<StartIntakeCommandHandler> _logger;

    public StartIntakeCommandHandler(
        IAppDbContext db,
        IMagicLinkAuthority authority,
        IAuditWriter audit,
        IDbExceptionTranslator dbExceptionTranslator,
        TimeProvider clock,
        ILogger<StartIntakeCommandHandler> logger)
    {
        _db = db;
        _authority = authority;
        _audit = audit;
        _dbExceptionTranslator = dbExceptionTranslator;
        _clock = clock;
        _logger = logger;
    }

    public async Task<StartIntakeResult> Handle(StartIntakeCommand request, CancellationToken ct)
    {
        if (request.ProviderId == Guid.Empty)
            throw new ArgumentException("ProviderId is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.ProviderEmail))
            throw new ArgumentException("ProviderEmail is required.", nameof(request));

        var nowUtc = _clock.GetUtcNow();

        // Existence check + double-start pre-check + budget snapshot in
        // two cheap queries. The UNIQUE (provider_id) on intake_sessions
        // is the floor for a race between two concurrent admin POSTs;
        // this pre-check surfaces the typed exception instead of letting
        // Npgsql's 23505 bubble as 500.
        //
        // IntakeBudgetTurns is copied onto the IntakeSession at start.
        // The property is immutable today (set only at Provider.Create);
        // the copy is a forward guarantee so a future setter can't
        // retroactively extend a running session. New providers get
        // whatever admin supplied at create time (or
        // Provider.DefaultIntakeBudgetTurns).
        //
        // FirstOrDefaultAsync (not Single) because Id is the PK —
        // uniqueness is schema-enforced, no LIMIT 2 sanity probe needed.
        var providerSnapshot = await _db.Providers
            .AsNoTracking()
            .Where(p => p.Id == request.ProviderId)
            .Select(p => new { p.IntakeBudgetTurns })
            .FirstOrDefaultAsync(ct);
        if (providerSnapshot is null)
            throw new ProviderNotFoundException(request.ProviderId);

        var alreadyStarted = await _db.IntakeSessions
            .AsNoTracking()
            .AnyAsync(s => s.ProviderId == request.ProviderId, ct);
        if (alreadyStarted)
            throw new IntakeAlreadyExistsException(request.ProviderId);

        var session = IntakeSession.Start(
            request.ProviderId,
            turnBudget: providerSnapshot.IntakeBudgetTurns,
            nowUtc: nowUtc);

        var link = MagicLink.Issue(request.ProviderId, issuedAt: nowUtc);
        var token = _authority.SignToken(link);

        // The intake-invitation outbound message rides on the same
        // transaction so the admin can't end up with a session +
        // magic link but no email queued for dispatch. The dispatcher
        // (C5) sends after the 10-minute admin yank window.
        //
        // The body embeds the signed token directly — the dispatcher
        // doesn't store URL templates centrally yet, and the demo
        // loop's Next.js portal reads `/portal/{token}` from the path.
        // A future polish (P6) replaces this string-concat with a
        // template that interpolates a configurable PORTAL_BASE_URL.
        var invitation = OutboundMessage.Compose(
            providerId: session.ProviderId,
            turnId: session.Id, // session id stands in pre-first-turn — there's no agent turn yet
            kind: MessageKind.IntakeInvitation,
            toAddress: request.ProviderEmail,
            subject: "PacketReady — your credentialing intake",
            body: BuildInvitationBody(token),
            composedAt: nowUtc);

        _db.IntakeSessions.Add(session);
        _db.MagicLinks.Add(link);
        _db.OutboundMessages.Add(invitation);

        // The audit row stages on the same scope so it's atomic with the
        // session + link + outbox row. A rollback drops all four together.
        _audit.Stage(AuditEvent.Create(
            eventType: AuditEventType.IntakeStarted,
            payloadJson: new IntakeStartedPayload(
                ProviderId: session.ProviderId,
                IntakeSessionId: session.Id,
                MagicLinkId: link.Id,
                ExpiresAt: link.ExpiresAt).ToJson(),
            providerId: session.ProviderId,
            occurredAt: nowUtc));

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (
            _dbExceptionTranslator.IsUniqueViolation(ex, IntakeProviderUniqueConstraint))
        {
            // The pre-check is sequential-safe but TOCTOU under concurrent
            // admin POSTs: two callers both see no row, both stage, both
            // SaveChanges — the loser hits ux_intake_sessions_provider.
            // Without this catch the loser would surface as a 500; with it
            // they get the same typed 409 the sequential path returns.
            _logger.LogInformation(
                "StartIntake lost the race for provider {ProviderId}; surfacing as IntakeAlreadyExists.",
                request.ProviderId);
            throw new IntakeAlreadyExistsException(request.ProviderId);
        }

        _logger.LogInformation(
            "Intake started for provider {ProviderId}: session={SessionId}, link={LinkId}, expires={ExpiresAt}",
            session.ProviderId, session.Id, link.Id, link.ExpiresAt);

        return new StartIntakeResult(
            ProviderId: session.ProviderId,
            IntakeSessionId: session.Id,
            MagicLinkId: link.Id,
            Token: token,
            ExpiresAt: link.ExpiresAt);
    }

    private static string BuildInvitationBody(string token) =>
        $"""
        Hi,

        You're starting your credentialing intake with PacketReady. Click the
        link below to upload your licensing documents. The link is valid for
        7 days.

            /portal/{token}

        You'll see what we already have on file (including anything we've
        already extracted) before submitting. Reach out if anything looks off.

        — PacketReady
        """;
}
