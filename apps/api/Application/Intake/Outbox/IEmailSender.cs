namespace PacketReady.Application.Intake.Outbox;

/// <summary>
/// Port for the outbound-email transport. Implemented by
/// <c>Infrastructure.Outbox.MockSmtpSender</c> in P5 (writes <c>.eml</c>
/// files + stdout) and by a real SMTP impl post-demo. The dispatcher takes
/// this dependency, not a concrete client — the transport is the swap
/// point when (and only when) a real reviewer needs an email.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(EmailEnvelope envelope, CancellationToken ct = default);
}

/// <summary>
/// What the sender needs to dispatch one message. Decoupled from
/// <c>OutboundMessage</c> so the dispatcher can fan out the
/// provider-email lookup (which lives in the persistence layer) without
/// teaching the sender about EF.
///
/// <para><see cref="MessageId"/> is the <c>OutboundMessage.Id</c> — used
/// as the filename stem in <c>MockSmtpSender</c>'s
/// <c>outbox/sent/{date}/{id}.eml</c> and as the RFC 5322 <c>Message-ID</c>
/// header for traceability in the dev demo loop.</para>
/// </summary>
public sealed record EmailEnvelope(
    Guid MessageId,
    string ToAddress,
    string FromAddress,
    string Subject,
    string Body,
    DateTimeOffset Date);
