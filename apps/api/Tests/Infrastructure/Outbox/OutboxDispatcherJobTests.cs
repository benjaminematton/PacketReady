using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using PacketReady.Application.Intake.Outbox;
using PacketReady.Domain.Messaging;
using PacketReady.Domain.Providers;
using PacketReady.Infrastructure.Audit;
using PacketReady.Infrastructure.Outbox;
using PacketReady.Infrastructure.Persistence;
using Xunit;

namespace PacketReady.Tests.Infrastructure.Outbox;

public class OutboxDispatcherJobTests : IDisposable
{
    private static readonly DateTimeOffset T0 =
        new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ProviderId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly InMemoryContextFactory _factory;
    private readonly PacketReadyDbContext _db;
    private readonly FakeTimeProvider _clock;
    private readonly Mock<IEmailSender> _sender;
    private readonly OutboxDispatcherJob _job;

    public OutboxDispatcherJobTests()
    {
        _factory = new InMemoryContextFactory(Guid.NewGuid().ToString());
        _db = _factory.CreateDbContext();
        _clock = new FakeTimeProvider(T0);
        _sender = new Mock<IEmailSender>(MockBehavior.Strict);

        var audit = new AuditWriter(_db, _factory, NullLogger<AuditWriter>.Instance);
        _job = new OutboxDispatcherJob(
            _db, _sender.Object, audit, _clock,
            NullLogger<OutboxDispatcherJob>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private async Task SeedProviderAsync()
    {
        var profile = ProviderProfile.Create(
            fullName: "Henry Anderson",
            dateOfBirth: new DateOnly(1980, 1, 15),
            npi: "1234567890",
            credentialingState: "CA",
            nowUtc: T0);
        _db.Providers.Add(Provider.CreateForTesting(ProviderId, profile, T0));
        await _db.SaveChangesAsync();
    }

    private OutboundMessage QueuedMessage(string toAddress = "provider@example.com", TimeSpan? hold = null)
        => OutboundMessage.Compose(
            providerId: ProviderId,
            turnId: Guid.NewGuid(),
            kind: MessageKind.Followup,
            toAddress: toAddress,
            subject: "subj",
            body: "body",
            composedAt: T0,
            holdDuration: hold ?? TimeSpan.FromMinutes(10));

    // ───────────────────────────────────────────── hold gate ────────────

    [Fact]
    public async Task RunAsync_DoesNotSendWhileHoldActive()
    {
        await SeedProviderAsync();
        _db.OutboundMessages.Add(QueuedMessage());   // held_until = T0 + 10 min
        await _db.SaveChangesAsync();

        _clock.SetUtcNow(T0.AddMinutes(5));  // before hold elapses

        await _job.RunAsync();

        _sender.Verify(s => s.SendAsync(It.IsAny<EmailEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Never);
        var msg = await _db.OutboundMessages.SingleAsync();
        Assert.Equal(OutboundMessageStatus.Queued, msg.Status);
    }

    [Fact]
    public async Task RunAsync_SendsAndMarksSent_OnceHoldElapses()
    {
        await SeedProviderAsync();
        _db.OutboundMessages.Add(QueuedMessage(toAddress: "henry@example.com"));
        await _db.SaveChangesAsync();

        _clock.SetUtcNow(T0.AddMinutes(11));   // past the 10-min hold

        EmailEnvelope? captured = null;
        _sender
            .Setup(s => s.SendAsync(It.IsAny<EmailEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<EmailEnvelope, CancellationToken>((env, _) => captured = env)
            .Returns(Task.CompletedTask);

        await _job.RunAsync();

        Assert.NotNull(captured);
        Assert.Equal("henry@example.com", captured!.ToAddress);
        Assert.Equal("subj", captured.Subject);

        var msg = await _factory.CreateDbContext().OutboundMessages.SingleAsync();
        Assert.Equal(OutboundMessageStatus.Sent, msg.Status);
        Assert.NotNull(msg.SentAt);
    }

    [Fact]
    public async Task RunAsync_LeavesCancelledRowsAlone()
    {
        await SeedProviderAsync();
        var msg = QueuedMessage();
        msg.Cancel();
        _db.OutboundMessages.Add(msg);
        await _db.SaveChangesAsync();

        _clock.SetUtcNow(T0.AddMinutes(11));

        await _job.RunAsync();

        _sender.Verify(s => s.SendAsync(It.IsAny<EmailEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ───────────────────────────────────────────── error isolation ──────

    [Fact]
    public async Task RunAsync_OneBadRow_DoesNotStallOthers()
    {
        await SeedProviderAsync();
        var bad = QueuedMessage(toAddress: "bad@example.com");
        var good = QueuedMessage(toAddress: "good@example.com");
        _db.OutboundMessages.Add(bad);
        _db.OutboundMessages.Add(good);
        await _db.SaveChangesAsync();

        _clock.SetUtcNow(T0.AddMinutes(11));

        _sender
            .Setup(s => s.SendAsync(
                It.Is<EmailEnvelope>(e => e.ToAddress == "bad@example.com"),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated SMTP outage"));
        _sender
            .Setup(s => s.SendAsync(
                It.Is<EmailEnvelope>(e => e.ToAddress == "good@example.com"),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _job.RunAsync();

        using var verify = _factory.CreateDbContext();
        var msgs = await verify.OutboundMessages
            .OrderBy(m => m.ToAddress)
            .ToListAsync();
        Assert.Equal(OutboundMessageStatus.Queued, msgs[0].Status);  // bad@ stays queued
        Assert.Equal(OutboundMessageStatus.Sent,   msgs[1].Status);  // good@ sent
    }

    // ───────────────────────────────────────────── batch cap ────────────

    [Fact]
    public async Task RunAsync_NoDueMessages_NoSenderCalls()
    {
        // Empty queue.
        await _job.RunAsync();

        _sender.Verify(s => s.SendAsync(It.IsAny<EmailEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
