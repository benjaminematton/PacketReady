using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PacketReady.Application.Abstractions;
using PacketReady.Application.Audit;
using PacketReady.Application.Intake.Outbox;
using PacketReady.Domain.Audit;
using PacketReady.Domain.Messaging;

namespace PacketReady.Infrastructure.Outbox;

/// <summary>
/// Recurring Hangfire job that drains the outbox. Pulls
/// <see cref="OutboundMessageStatus.Queued"/> rows whose
/// <c>held_until</c> has elapsed, dispatches them through
/// <see cref="IEmailSender"/>, and flips the status to
/// <see cref="OutboundMessageStatus.Sent"/>. One row per iteration so a
/// poisoned message can't take down the queue — exceptions are caught
/// per-row and logged.
///
/// <para>The 10-minute hold-at-send TTL lives in the SELECT clause
/// (<c>held_until &lt;= now()</c>): the dispatcher physically cannot send
/// a row before the admin yank window elapses, even if a clock skew
/// makes <see cref="OutboundMessage.MarkSent"/>'s in-aggregate check
/// disagree.</para>
///
/// <para><b>Hangfire retry posture.</b> Default — Hangfire retries on
/// transient failure. The outbox is naturally idempotent: a row that
/// already flipped to <c>Sent</c> falls out of the SELECT, so a
/// duplicate firing of the job is a no-op. <c>MockSmtpSender</c>'s
/// <c>FileMode.CreateNew</c> + the dedup UNIQUE on
/// <c>(provider_id, turn_id, kind)</c> are belt-and-braces against
/// double-send.</para>
/// </summary>
public sealed class OutboxDispatcherJob
{
    /// <summary>Hangfire's <c>RecurringJobManager.AddOrUpdate</c> registration id.</summary>
    public const string RecurringJobId = "outbox-dispatcher";

    /// <summary>Default poll cadence. The phase-5 doc names 30 seconds.</summary>
    public const string DefaultCron = "*/30 * * * * *";  // every 30s — Hangfire cron with seconds

    /// <summary>
    /// Cap on how many rows we drain per iteration. Bounds the worst-case
    /// latency a single bad message can inflict, and keeps the audit-log
    /// fanout per-tick bounded.
    /// </summary>
    public const int MaxBatchSize = 50;

    private readonly IAppDbContext _db;
    private readonly IEmailSender _sender;
    private readonly IAuditWriter _audit;
    private readonly TimeProvider _clock;
    private readonly ILogger<OutboxDispatcherJob> _logger;

    public OutboxDispatcherJob(
        IAppDbContext db,
        IEmailSender sender,
        IAuditWriter audit,
        TimeProvider clock,
        ILogger<OutboxDispatcherJob> logger)
    {
        _db = db;
        _sender = sender;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();

        // The status enum stores 'Queued' (PascalCase) per
        // OutboundMessageConfiguration. Filter by both status + hold;
        // both are indexed.
        var due = await _db.OutboundMessages
            .Where(m => m.Status == OutboundMessageStatus.Queued && m.HeldUntil <= now)
            .OrderBy(m => m.HeldUntil)
            .Take(MaxBatchSize)
            .ToListAsync(ct);

        if (due.Count == 0) return;

        _logger.LogInformation(
            "OutboxDispatcher draining {Count} due messages", due.Count);

        // Per-row try/catch — one poison row can't stall the queue.
        // Failures stay Queued for the next tick. Persistent failures
        // need operator attention (no automatic dead-letter; surfaces
        // as a row that keeps appearing in the dispatcher's logs).
        foreach (var msg in due)
        {
            try
            {
                await DispatchOneAsync(msg, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "OutboxDispatcher failed on message {MessageId} (provider {ProviderId}, kind {Kind})",
                    msg.Id, msg.ProviderId, msg.Kind);
                // Don't rethrow; let the loop continue.
            }
        }
    }

    private async Task DispatchOneAsync(OutboundMessage msg, CancellationToken ct)
    {
        var sentAt = _clock.GetUtcNow();

        await _sender.SendAsync(
            new EmailEnvelope(
                MessageId: msg.Id,
                ToAddress: msg.ToAddress,
                FromAddress: "noreply@packetready.local",
                Subject: msg.Subject,
                Body: msg.Body,
                Date: sentAt),
            ct);

        // Mark Sent in the aggregate (re-checks the hold window
        // defense-in-depth) then save. A Sent row is the durable
        // dispatch receipt — paired with the .eml file the mock SMTP
        // wrote.
        msg.MarkSent(sentAt);

        _audit.Stage(AuditEvent.Create(
            eventType: AuditEventType.OutboundMessageSent,
            payloadJson: System.Text.Json.JsonSerializer.Serialize(new
            {
                outbound_message_id = msg.Id,
                provider_id = msg.ProviderId,
                turn_id = msg.TurnId,
                kind = msg.Kind.ToString(),
                to_address = msg.ToAddress,
                sent_at = sentAt,
            }),
            providerId: msg.ProviderId,
            turnId: msg.TurnId,
            occurredAt: sentAt));

        await _db.SaveChangesAsync(ct);
    }
}
