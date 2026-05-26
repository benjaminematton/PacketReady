using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PacketReady.Application.Abstractions;
using PacketReady.Application.Audit;
using PacketReady.Application.Intake.Agent;
using PacketReady.Domain.Audit;
using PacketReady.Domain.Intake;
using PacketReady.Domain.MagicLinks;
using PacketReady.Domain.Messaging;

namespace PacketReady.Infrastructure.Intake;

/// <summary>
/// Hangfire job — one provider's agent turn. Enqueued from
/// <c>PortalSubmitEndpoint</c> when a magic-link submission lands, and
/// from itself when an external orchestrator wants to retry. The job
/// drives a single end-to-end transaction:
///
/// <list type="number">
///   <item>Load the <see cref="IntakeSession"/> for the provider.</item>
///   <item>If <c>TurnsConsumed >= TurnBudget</c>, escalate + return.</item>
///   <item><see cref="IntakeSession.BeginAgentTurn"/>, save (commits the
///         turn-counter bump so a retry sees the consumed turn).</item>
///   <item>Run the agent (<see cref="IIntakeAgent.RunTurnAsync"/>).</item>
///   <item>Apply the transition via
///         <see cref="IntakeStateTransitioner"/> (complete / propose
///         followup + new magic link + outbox row / escalate).</item>
///   <item>Save + audit.</item>
/// </list>
///
/// <para><b>Concurrency.</b> The doc's spec calls for
/// <c>SELECT … FOR UPDATE</c> on the <c>intake_sessions</c> row to
/// serialize two-worker races. Deferred — the in-memory
/// <see cref="IntakeSession"/> state machine refuses
/// <c>BeginAgentTurn</c> from any state but
/// <see cref="IntakeState.AwaitingProvider"/>, so the second worker
/// throws and Hangfire fails the duplicate without corrupting state.
/// Add FOR UPDATE in a follow-up once a real concurrent-failure trace
/// shows it's load-bearing.</para>
///
/// <para><b>Hangfire retry posture.</b> [AutomaticRetry(Attempts=0)] —
/// turns are stateful and idempotent only in the "did anything change?"
/// sense. A failed turn already moved <c>TurnsConsumed</c>; re-firing
/// would burn another budget axis. Hangfire's default 10-retry policy
/// is wrong here.</para>
/// </summary>
[AutomaticRetry(Attempts = 0)]
public sealed class IntakeTurnJob
{
    private readonly IAppDbContext _db;
    private readonly IIntakeAgent _agent;
    private readonly IntakeStateTransitioner _transitioner;
    private readonly IAuditWriter _audit;
    private readonly TimeProvider _clock;
    private readonly ILogger<IntakeTurnJob> _logger;

    public IntakeTurnJob(
        IAppDbContext db,
        IIntakeAgent agent,
        IntakeStateTransitioner transitioner,
        IAuditWriter audit,
        TimeProvider clock,
        ILogger<IntakeTurnJob> logger)
    {
        _db = db;
        _agent = agent;
        _transitioner = transitioner;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Drive one agent turn for <paramref name="providerId"/>. Surface
    /// for both Hangfire (<c>BackgroundJob.Enqueue</c>) and tests.
    /// </summary>
    public async Task RunAsync(Guid providerId, CancellationToken ct = default)
    {
        if (providerId == Guid.Empty)
            throw new ArgumentException("providerId is required.", nameof(providerId));

        var nowUtc = _clock.GetUtcNow();

        var session = await _db.IntakeSessions
            .SingleOrDefaultAsync(s => s.ProviderId == providerId, ct);
        if (session is null)
        {
            _logger.LogWarning(
                "IntakeTurnJob fired for provider {ProviderId} but no intake_sessions row found; skipping.",
                providerId);
            return;
        }

        // Pre-check the turn budget. The aggregate's BeginAgentTurn refuses
        // the (budget+1)th call anyway, but escalating here surfaces a
        // typed FSM transition instead of an exception bubbling out of
        // the loop.
        if (session.TurnsConsumed >= session.TurnBudget)
        {
            _logger.LogInformation(
                "Intake budget exhausted for provider {ProviderId} ({Consumed}/{Budget}); escalating.",
                providerId, session.TurnsConsumed, session.TurnBudget);
            session.Escalate("turn-budget-exhausted", nowUtc);
            await _db.SaveChangesAsync(ct);
            return;
        }

        // Begin the turn. Commits the turn-counter bump independently
        // of the agent run so a Hangfire crash mid-agent doesn't lose
        // the "we attempted this" signal.
        var turnId = Guid.NewGuid();
        session.BeginAgentTurn(turnId, nowUtc);
        await _db.SaveChangesAsync(ct);

        try
        {
            var result = await _agent.RunTurnAsync(providerId, turnId, ct);

            // Followup needs an email destination. The latest sent /
            // queued intake_invitation or followup carries it. We don't
            // hit the providers table for an email column — see C5
            // commit notes.
            var toAddress = await GetMostRecentToAddressAsync(providerId, ct);

            var effect = _transitioner.Apply(session, result, toAddress, _clock.GetUtcNow());

            _audit.Stage(AuditEvent.Create(
                eventType: AuditEventType.IntakeTurnCompleted,
                payloadJson: System.Text.Json.JsonSerializer.Serialize(new
                {
                    provider_id = providerId,
                    turn_id = turnId,
                    is_terminal = result.IsTerminal,
                    completed_readiness_score_id = result.CompletedReadinessScoreId,
                    queued_outbound_message_id = effect.QueuedOutboundMessageId,
                    new_magic_link_id = effect.NewMagicLinkId,
                    steps = result.StepsConsumed,
                    input_tokens = result.InputTokensConsumed,
                    output_tokens = result.OutputTokensConsumed,
                    wall_clock_ms = (int)result.WallClockConsumed.TotalMilliseconds,
                }),
                providerId: providerId,
                turnId: turnId,
                occurredAt: _clock.GetUtcNow()));

            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Intake turn complete for provider {ProviderId}: terminal={IsTerminal}, followup={HasFollowup}, steps={Steps}, tokens={InTok}+{OutTok}",
                providerId, result.IsTerminal, result.HasProposedFollowup,
                result.StepsConsumed, result.InputTokensConsumed, result.OutputTokensConsumed);
        }
        catch (BudgetExhaustedException ex)
        {
            _logger.LogWarning(
                "Intake turn for provider {ProviderId} exhausted budget axis '{Axis}'; escalating.",
                providerId, ex.Axis);

            session.Escalate($"budget:{ex.Axis}", _clock.GetUtcNow());
            await _db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Lookup helper — pull the most recent <see cref="OutboundMessage.ToAddress"/>
    /// for this provider so a followup composed by the agent reaches
    /// the same destination as the original intake invitation. Falls
    /// back to a placeholder that the dispatcher's header-injection
    /// guard will refuse to send — surfaces "no email on file" as a
    /// loud dispatch error rather than a silent misroute.
    /// </summary>
    private async Task<string> GetMostRecentToAddressAsync(Guid providerId, CancellationToken ct)
    {
        var address = await _db.OutboundMessages
            .AsNoTracking()
            .Where(m => m.ProviderId == providerId)
            .OrderByDescending(m => m.ComposedAt)
            .Select(m => m.ToAddress)
            .FirstOrDefaultAsync(ct);
        return address ?? "unknown@example.invalid";
    }
}
