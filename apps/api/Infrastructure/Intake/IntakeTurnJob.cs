using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PacketReady.Application.Abstractions;
using PacketReady.Application.Audit;
using PacketReady.Application.Intake.Agent;
using PacketReady.Application.Intake.Audit;
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
[Queue(QueueName)]
public sealed class IntakeTurnJob
{
    /// <summary>
    /// Hangfire queue name. <c>IntakeTurnJob</c> can hold a worker for the
    /// full per-turn wall-clock budget; segregating it from
    /// <see cref="OutboxDispatcherJob"/> keeps the dispatcher's recurring
    /// 30-second tick from starving behind a slow agent turn.
    /// </summary>
    public const string QueueName = "agent-turns";

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

            var payload = new IntakeTurnCompletedPayload(
                ProviderId: providerId,
                TurnId: turnId,
                IsTerminal: result.IsTerminal,
                CompletedReadinessScoreId: result.CompletedReadinessScoreId,
                QueuedOutboundMessageId: effect.QueuedOutboundMessageId,
                NewMagicLinkId: effect.NewMagicLinkId,
                Steps: result.StepsConsumed,
                InputTokens: result.InputTokensConsumed,
                OutputTokens: result.OutputTokensConsumed,
                WallClockMs: (int)result.WallClockConsumed.TotalMilliseconds);

            _audit.Stage(AuditEvent.Create(
                eventType: AuditEventType.IntakeTurnCompleted,
                payloadJson: payload.ToJson(),
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutdown, not a job failure — let Hangfire reschedule the
            // turn on a future worker. BeginAgentTurn's bump is already
            // committed, so the rescheduled run will see TurnsConsumed
            // advanced. Acceptable: a clean shutdown costs one turn-budget
            // slot, not a stuck session.
            throw;
        }
        catch (Exception ex)
        {
            // Anything else (LLM 429/5xx, socket error, tool contract
            // violation, …). BeginAgentTurn already moved the session into
            // AgentProcessing; [AutomaticRetry(Attempts=0)] means Hangfire
            // won't redrive us, so without this branch the session sits
            // stuck and the provider can't resubmit (link consumed at
            // portal time). Escalate with the exception type so an admin
            // can recover from intake_sessions audit alone.
            _logger.LogError(ex,
                "Intake turn for provider {ProviderId} (turn {TurnId}) failed with {ExceptionType}; escalating.",
                providerId, turnId, ex.GetType().Name);

            session.Escalate($"agent-error:{ex.GetType().Name}", _clock.GetUtcNow());
            await _db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Lookup helper — pull the most recent <see cref="OutboundMessage.ToAddress"/>
    /// for this provider so a followup composed by the agent reaches the
    /// same destination as the original intake invitation. Throws when no
    /// prior outbox row exists: <c>StartIntakeCommandHandler</c> guarantees
    /// the <c>IntakeInvitation</c> row is written in the same transaction
    /// as the session, so a missing row means a corrupt setup — surfacing
    /// it loud (via the outer escalate branch) beats sending followups to
    /// a sentinel address.
    /// </summary>
    private async Task<string> GetMostRecentToAddressAsync(Guid providerId, CancellationToken ct)
    {
        var address = await _db.OutboundMessages
            .AsNoTracking()
            .Where(m => m.ProviderId == providerId)
            .OrderByDescending(m => m.ComposedAt)
            .Select(m => m.ToAddress)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(address))
            throw new InvalidOperationException(
                $"No outbound_messages row found for provider {providerId}; " +
                "StartIntakeCommandHandler should have written the intake_invitation " +
                "atomically with the session.");
        return address;
    }
}
