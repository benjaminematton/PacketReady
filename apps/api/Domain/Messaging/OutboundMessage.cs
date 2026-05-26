namespace PacketReady.Domain.Messaging;

/// <summary>
/// One outbound message in the hold-at-send outbox. Aggregate root.
///
/// <para><b>Lifecycle.</b> Composed by the intake agent's
/// <c>compose_followup</c> tool (or by <c>StartIntakeCommand</c> for the
/// initial invitation). Lands as <see cref="OutboundMessageStatus.Queued"/>
/// with <see cref="HeldUntil"/> = <see cref="ComposedAt"/> + 10 min — the
/// admin's yank window. The dispatcher
/// (<c>OutboxDispatcherJob</c>, P5 C5) sends rows where
/// <c>status = 'Queued' AND held_until &lt;= now()</c>, then flips to
/// <see cref="OutboundMessageStatus.Sent"/>. An admin yank inside the
/// window flips to <see cref="OutboundMessageStatus.Cancelled"/> instead;
/// the dispatcher physically cannot send a row before <see cref="HeldUntil"/>
/// elapses (the predicate is in the SELECT clause, per design.md §7.5).</para>
///
/// <para><b>Dedup.</b> The <c>UNIQUE (provider_id, turn_id, kind)</c> index
/// in <c>OutboundMessageConfiguration</c> catches re-enqueues from a
/// retried <c>IntakeTurnJob</c>. The outbox handler swallows the
/// <c>unique_violation</c> as a "dedup hit" — same pattern as
/// <c>ExtractionPersister</c> from P3.</para>
/// </summary>
public sealed class OutboundMessage
{
    /// <summary>
    /// 10-minute hold window from design.md §7.5. The aggregate accepts a
    /// caller-supplied hold for tests / future tuning, but the default is
    /// the spec's load-bearing constant — production callers should not pass
    /// a different value without a paired design decision.
    /// </summary>
    public static readonly TimeSpan DefaultHoldDuration = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Column cap on <c>outbound_messages.subject</c>. Mirrored in
    /// <c>OutboundMessageConfiguration</c>; enforced in <see cref="Compose"/>
    /// so callers fail at the factory rather than at <c>SaveChanges</c>.
    /// </summary>
    public const int MaxSubjectLength = 256;

    /// <summary>
    /// Column cap on <c>outbound_messages.to_address</c>. RFC 5321 §4.5.3.1.3
    /// caps path length at 256; we use the same to catch malformed input
    /// at the factory.
    /// </summary>
    public const int MaxToAddressLength = 256;

    public Guid Id { get; private set; }
    public Guid ProviderId { get; private set; }
    public Guid TurnId { get; private set; }
    public MessageKind Kind { get; private set; }

    /// <summary>
    /// Destination email address. Per-message rather than looked up from
    /// <c>Provider</c> at dispatch time: each <c>OutboundMessage</c> is
    /// the durable record of who we tried to reach, so a Provider record
    /// renamed mid-dispatch can't retroactively misroute a queued row.
    /// </summary>
    public string ToAddress { get; private set; } = null!;

    public string Subject { get; private set; } = null!;
    public string Body { get; private set; } = null!;
    public OutboundMessageStatus Status { get; private set; }
    public DateTimeOffset HeldUntil { get; private set; }
    public DateTimeOffset ComposedAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }

    private OutboundMessage() { }

    /// <summary>
    /// Compose a new outbound message. Lands as <c>Queued</c> with
    /// <see cref="HeldUntil"/> = <paramref name="composedAt"/> +
    /// <paramref name="holdDuration"/> (defaults to
    /// <see cref="DefaultHoldDuration"/>).
    /// </summary>
    public static OutboundMessage Compose(
        Guid providerId,
        Guid turnId,
        MessageKind kind,
        string toAddress,
        string subject,
        string body,
        DateTimeOffset composedAt,
        TimeSpan? holdDuration = null)
    {
        if (providerId == Guid.Empty)
            throw new ArgumentException("Provider id is required.", nameof(providerId));
        if (turnId == Guid.Empty)
            throw new ArgumentException("Turn id is required.", nameof(turnId));
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(
                nameof(kind), kind, "Unknown MessageKind.");
        if (string.IsNullOrWhiteSpace(toAddress))
            throw new ArgumentException("toAddress is required.", nameof(toAddress));
        if (toAddress.Length > MaxToAddressLength)
            throw new ArgumentException(
                $"toAddress must be {MaxToAddressLength} characters or fewer (got {toAddress.Length}).",
                nameof(toAddress));
        if (toAddress.IndexOfAny(['\r', '\n']) >= 0)
            throw new ArgumentException(
                "toAddress must not contain CR/LF (header-injection guard).",
                nameof(toAddress));
        if (string.IsNullOrWhiteSpace(subject))
            throw new ArgumentException("Subject is required.", nameof(subject));
        if (subject.Length > MaxSubjectLength)
            throw new ArgumentException(
                $"Subject must be {MaxSubjectLength} characters or fewer (got {subject.Length}).",
                nameof(subject));
        if (string.IsNullOrWhiteSpace(body))
            throw new ArgumentException("Body is required.", nameof(body));

        var hold = holdDuration ?? DefaultHoldDuration;
        if (hold < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(holdDuration), hold, "Hold duration cannot be negative.");

        return new OutboundMessage
        {
            Id = Guid.NewGuid(),
            ProviderId = providerId,
            TurnId = turnId,
            Kind = kind,
            ToAddress = toAddress,
            Subject = subject,
            Body = body,
            Status = OutboundMessageStatus.Queued,
            ComposedAt = composedAt,
            HeldUntil = composedAt + hold,
            SentAt = null,
        };
    }

    /// <summary>
    /// True iff the message is <see cref="OutboundMessageStatus.Queued"/>
    /// and the hold window has elapsed. The dispatcher uses this filter in
    /// its read query (<c>status = 'Queued' AND held_until &lt;= now()</c>),
    /// but the aggregate also exposes it so the dispatcher's commit path
    /// can re-check after the SELECT under the row lock — a clock skew or a
    /// late admin yank between SELECT and dispatch must still be rejected.
    /// </summary>
    public bool IsReadyToSend(DateTimeOffset now)
        => Status == OutboundMessageStatus.Queued && HeldUntil <= now;

    /// <summary>
    /// Transition <c>Queued</c> → <c>Sent</c>. Refuses to flip a message
    /// whose hold window hasn't elapsed (defense-in-depth on the dispatcher
    /// query filter) or a message in any non-Queued state (cancelled-then-sent
    /// would silently overwrite the admin yank).
    /// </summary>
    public void MarkSent(DateTimeOffset sentAt)
    {
        if (Status != OutboundMessageStatus.Queued)
            throw new InvalidOperationException(
                $"MarkSent requires status Queued, but message is {Status}.");

        if (sentAt < HeldUntil)
            throw new InvalidOperationException(
                $"MarkSent at {sentAt:o} would breach the hold window (held_until = {HeldUntil:o}).");

        Status = OutboundMessageStatus.Sent;
        SentAt = sentAt;
    }

    /// <summary>
    /// Admin yank: <c>Queued</c> → <c>Cancelled</c>. Only meaningful inside
    /// the hold window — a cancellation after dispatch is a no-op since the
    /// status is already <c>Sent</c>. Refused from non-Queued states to
    /// catch double-yank or yank-after-send races.
    /// </summary>
    public void Cancel()
    {
        if (Status != OutboundMessageStatus.Queued)
            throw new InvalidOperationException(
                $"Cancel requires status Queued, but message is {Status}.");

        Status = OutboundMessageStatus.Cancelled;
    }
}
