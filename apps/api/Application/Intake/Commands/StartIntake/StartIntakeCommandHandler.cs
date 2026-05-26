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

namespace PacketReady.Application.Intake.Commands.StartIntake;

public sealed class StartIntakeCommandHandler : IRequestHandler<StartIntakeCommand, StartIntakeResult>
{
    private readonly IAppDbContext _db;
    private readonly IMagicLinkAuthority _authority;
    private readonly IAuditWriter _audit;
    private readonly TimeProvider _clock;
    private readonly ILogger<StartIntakeCommandHandler> _logger;

    public StartIntakeCommandHandler(
        IAppDbContext db,
        IMagicLinkAuthority authority,
        IAuditWriter audit,
        TimeProvider clock,
        ILogger<StartIntakeCommandHandler> logger)
    {
        _db = db;
        _authority = authority;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    public async Task<StartIntakeResult> Handle(StartIntakeCommand request, CancellationToken ct)
    {
        if (request.ProviderId == Guid.Empty)
            throw new ArgumentException("ProviderId is required.", nameof(request));

        var nowUtc = _clock.GetUtcNow();

        // Existence check + double-start pre-check in two cheap queries. The
        // UNIQUE (provider_id) on intake_sessions is the floor for a race
        // between two concurrent admin POSTs; this pre-check surfaces the
        // typed exception instead of letting Npgsql's 23505 bubble as 500.
        var providerExists = await _db.Providers
            .AsNoTracking()
            .AnyAsync(p => p.Id == request.ProviderId, ct);
        if (!providerExists)
            throw new ProviderNotFoundException(request.ProviderId);

        var alreadyStarted = await _db.IntakeSessions
            .AsNoTracking()
            .AnyAsync(s => s.ProviderId == request.ProviderId, ct);
        if (alreadyStarted)
            throw new IntakeAlreadyExistsException(request.ProviderId);

        var session = IntakeSession.Start(
            request.ProviderId,
            turnBudget: IntakeSession.DefaultTurnBudget,
            nowUtc: nowUtc);

        var link = MagicLink.Issue(request.ProviderId, issuedAt: nowUtc);
        var token = _authority.SignToken(link);

        _db.IntakeSessions.Add(session);
        _db.MagicLinks.Add(link);

        // The audit row stages on the same scope so it's atomic with the
        // session + link. A rollback drops all three together.
        _audit.Stage(AuditEvent.Create(
            eventType: AuditEventType.IntakeStarted,
            payloadJson: new IntakeStartedPayload(
                ProviderId: session.ProviderId,
                IntakeSessionId: session.Id,
                MagicLinkId: link.Id,
                ExpiresAt: link.ExpiresAt).ToJson(),
            providerId: session.ProviderId,
            occurredAt: nowUtc));

        await _db.SaveChangesAsync(ct);

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
}
