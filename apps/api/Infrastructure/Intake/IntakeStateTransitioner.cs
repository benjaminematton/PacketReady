using PacketReady.Application.Abstractions;
using PacketReady.Application.Intake.Agent;
using PacketReady.Application.Intake.MagicLinks;
using PacketReady.Domain.Intake;
using PacketReady.Domain.MagicLinks;
using PacketReady.Domain.Messaging;

namespace PacketReady.Infrastructure.Intake;

/// <summary>
/// Maps one <see cref="AgentTurnResult"/> onto the FSM transition + the
/// side effects (issue a fresh magic link, queue a followup outbound
/// message) the orchestrator commits as part of <c>IntakeTurnJob</c>.
///
/// <para>Pure-code in the sense that it doesn't drive the DB itself —
/// callers add the new entities to their <c>IAppDbContext</c> and save
/// in their own transaction. Splitting this out of <c>IntakeTurnJob</c>
/// keeps the FSM mapping testable without spinning up Hangfire.</para>
///
/// <para><b>Outcome → side effects:</b></para>
/// <list type="bullet">
///   <item><b>Terminal</b> (<c>result.IsTerminal</c>): the agent invoked
///         <c>compute_readiness</c>. Transition session →
///         <c>Complete</c>; no new magic link, no new outbound.</item>
///   <item><b>Followup proposed</b>: the agent composed an email.
///         Issue a fresh <see cref="MagicLink"/>, queue an
///         <see cref="OutboundMessage"/> with the proposed subject +
///         body, transition session → <c>AwaitingProvider</c> with the
///         new link id.</item>
///   <item><b>Empty turn</b>: the agent returned without a tool call or
///         a followup. Escalate so an admin reviews.</item>
/// </list>
/// </summary>
public sealed class IntakeStateTransitioner
{
    private readonly IAppDbContext _db;
    private readonly IMagicLinkAuthority _authority;

    public IntakeStateTransitioner(IAppDbContext db, IMagicLinkAuthority authority)
    {
        _db = db;
        _authority = authority;
    }

    /// <summary>
    /// What the transition produced. <see cref="NewMagicLinkToken"/> is
    /// the signed token for the freshly-issued link on a continuation —
    /// the orchestrator will stamp it into the queued
    /// <c>OutboundMessage.Body</c> at outbox-compose time. Both
    /// <c>NewMagicLink*</c> properties are null on terminal + empty
    /// turns.
    /// </summary>
    public sealed record TransitionEffect(
        Guid? NewMagicLinkId,
        string? NewMagicLinkToken,
        Guid? QueuedOutboundMessageId);

    public TransitionEffect Apply(
        IntakeSession session,
        AgentTurnResult result,
        string toAddress,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(result);

        if (result.IsTerminal)
        {
            if (result.CompletedReadinessScoreId is not { } scoreId)
                throw new InvalidOperationException(
                    "AgentTurnResult.IsTerminal but CompletedReadinessScoreId is null.");

            session.Complete(scoreId, nowUtc);
            return new TransitionEffect(null, null, null);
        }

        if (result.HasProposedFollowup)
        {
            if (string.IsNullOrWhiteSpace(toAddress))
                throw new ArgumentException(
                    "toAddress is required to queue a followup OutboundMessage.",
                    nameof(toAddress));

            var newLink = MagicLink.Issue(session.ProviderId, issuedAt: nowUtc);
            var token = _authority.SignToken(newLink);
            _db.MagicLinks.Add(newLink);

            var followup = OutboundMessage.Compose(
                providerId: session.ProviderId,
                turnId: result.TurnId,
                kind: MessageKind.Followup,
                toAddress: toAddress,
                subject: result.ProposedFollowupSubject!,
                body: result.ProposedFollowupBody!,
                composedAt: nowUtc);
            _db.OutboundMessages.Add(followup);

            session.EndAgentTurn(
                new AgentTurnOutcome { ContinueWithMagicLinkId = newLink.Id },
                nowUtc);

            return new TransitionEffect(newLink.Id, token, followup.Id);
        }

        // Empty turn — the agent didn't terminate or propose a followup.
        // Escalate so an admin reviews what the agent had. The partial
        // profile is whatever the aggregator can pull from extractions
        // at admin-review time; we don't snapshot it here.
        session.Escalate(
            reason: "agent-empty-turn",
            nowUtc: nowUtc);
        return new TransitionEffect(null, null, null);
    }
}
