using PacketReady.Domain.Messaging;
using Xunit;

namespace PacketReady.Tests.Domain.Messaging;

public class OutboundMessageTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid ProviderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TurnId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string ToAddr = "provider@example.com";

    // ─────────────────────────────────────────────────────── Compose ────

    [Fact]
    public void Compose_LandsAsQueuedWithHeldUntilFromComposedAtPlusHold()
    {
        var msg = OutboundMessage.Compose(
            ProviderId, TurnId, MessageKind.Followup,
            toAddress: ToAddr,
            subject: "we need a few things",
            body: "please upload your DEA",
            composedAt: T0,
            holdDuration: TimeSpan.FromMinutes(10));

        Assert.NotEqual(Guid.Empty, msg.Id);
        Assert.Equal(ProviderId, msg.ProviderId);
        Assert.Equal(TurnId, msg.TurnId);
        Assert.Equal(MessageKind.Followup, msg.Kind);
        Assert.Equal(ToAddr, msg.ToAddress);
        Assert.Equal("we need a few things", msg.Subject);
        Assert.Equal("please upload your DEA", msg.Body);
        Assert.Equal(OutboundMessageStatus.Queued, msg.Status);
        Assert.Equal(T0, msg.ComposedAt);
        Assert.Equal(T0.AddMinutes(10), msg.HeldUntil);
        Assert.Null(msg.SentAt);
    }

    [Fact]
    public void Compose_DefaultsHoldDurationToTenMinutes()
    {
        var msg = OutboundMessage.Compose(
            ProviderId, TurnId, MessageKind.IntakeInvitation,
            ToAddr, "subj", "body", composedAt: T0);

        Assert.Equal(T0 + OutboundMessage.DefaultHoldDuration, msg.HeldUntil);
        Assert.Equal(TimeSpan.FromMinutes(10), OutboundMessage.DefaultHoldDuration);
    }

    [Fact]
    public void Compose_RejectsEmptyProviderId()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            OutboundMessage.Compose(Guid.Empty, TurnId, MessageKind.Followup, ToAddr, "s", "b", T0));
        Assert.Equal("providerId", ex.ParamName);
    }

    [Fact]
    public void Compose_RejectsEmptyTurnId()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            OutboundMessage.Compose(ProviderId, Guid.Empty, MessageKind.Followup, ToAddr, "s", "b", T0));
        Assert.Equal("turnId", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Compose_RejectsBlankToAddress(string toAddress)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            OutboundMessage.Compose(ProviderId, TurnId, MessageKind.Followup, toAddress, "s", "b", T0));
        Assert.Equal("toAddress", ex.ParamName);
    }

    [Fact]
    public void Compose_RejectsToAddressOverColumnCap()
    {
        var oversize = new string('x', OutboundMessage.MaxToAddressLength + 1);
        var ex = Assert.Throws<ArgumentException>(() =>
            OutboundMessage.Compose(ProviderId, TurnId, MessageKind.Followup, oversize, "s", "b", T0));
        Assert.Equal("toAddress", ex.ParamName);
    }

    [Theory]
    [InlineData("a@b.com\r\nBcc: x@y.com")]
    [InlineData("a@b.com\nX-Smuggled: 1")]
    public void Compose_RejectsCrlfInToAddress(string toAddress)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            OutboundMessage.Compose(ProviderId, TurnId, MessageKind.Followup, toAddress, "s", "b", T0));
        Assert.Equal("toAddress", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Compose_RejectsBlankSubject(string subject)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            OutboundMessage.Compose(ProviderId, TurnId, MessageKind.Followup, ToAddr, subject, "body", T0));
        Assert.Equal("subject", ex.ParamName);
    }

    [Fact]
    public void Compose_RejectsSubjectOverColumnCap()
    {
        // Fail at the factory rather than letting EF surface a column-cap
        // violation at SaveChanges, where the stack trace doesn't point at
        // the caller that composed the over-long subject.
        var oversize = new string('x', OutboundMessage.MaxSubjectLength + 1);
        var ex = Assert.Throws<ArgumentException>(() =>
            OutboundMessage.Compose(ProviderId, TurnId, MessageKind.Followup, ToAddr, oversize, "body", T0));
        Assert.Equal("subject", ex.ParamName);
    }

    [Fact]
    public void Compose_AcceptsSubjectAtColumnCap()
    {
        var atCap = new string('x', OutboundMessage.MaxSubjectLength);
        var msg = OutboundMessage.Compose(
            ProviderId, TurnId, MessageKind.Followup, ToAddr, atCap, "body", T0);
        Assert.Equal(atCap, msg.Subject);
    }

    [Fact]
    public void Compose_RejectsBodyOverSoftCap()
    {
        // An LLM-composed followup that runs away shouldn't be able to
        // stuff megabytes into outbound_messages.body — cap at the factory
        // so the audit-log fanout stays predictable.
        var oversize = new string('x', OutboundMessage.MaxBodyLength + 1);
        var ex = Assert.Throws<ArgumentException>(() =>
            OutboundMessage.Compose(
                ProviderId, TurnId, MessageKind.Followup, ToAddr, "subj", oversize, T0));
        Assert.Equal("body", ex.ParamName);
    }

    [Fact]
    public void Compose_AcceptsBodyAtSoftCap()
    {
        var atCap = new string('x', OutboundMessage.MaxBodyLength);
        var msg = OutboundMessage.Compose(
            ProviderId, TurnId, MessageKind.Followup, ToAddr, "subj", atCap, T0);
        Assert.Equal(atCap, msg.Body);
    }

    [Fact]
    public void Compose_RejectsUndefinedKind()
    {
        // Out-of-range cast — DB CHECK would catch it too, but failing at
        // the factory points at the caller instead of at SaveChanges.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            OutboundMessage.Compose(
                ProviderId, TurnId, (MessageKind)999, ToAddr, "subject", "body", T0));
        Assert.Equal("kind", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Compose_RejectsBlankBody(string body)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            OutboundMessage.Compose(ProviderId, TurnId, MessageKind.Followup, ToAddr, "subject", body, T0));
        Assert.Equal("body", ex.ParamName);
    }

    [Fact]
    public void Compose_RejectsNegativeHoldDuration()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            OutboundMessage.Compose(
                ProviderId, TurnId, MessageKind.Followup, ToAddr, "s", "b", T0,
                holdDuration: TimeSpan.FromSeconds(-1)));
        Assert.Equal("holdDuration", ex.ParamName);
    }

    [Fact]
    public void Compose_AcceptsZeroHoldDuration()
    {
        // Zero is the boundary — an immediately-sendable message. Tests that
        // pin "the dispatcher sends X" want this so they don't have to
        // advance the clock; we allow it because the aggregate is still in
        // Queued and the dispatcher's filter runs the predicate.
        var msg = OutboundMessage.Compose(
            ProviderId, TurnId, MessageKind.Followup, ToAddr, "s", "b", T0,
            holdDuration: TimeSpan.Zero);

        Assert.Equal(T0, msg.HeldUntil);
    }

    // ─────────────────────────────────────────────── IsReadyToSend ──────

    [Fact]
    public void IsReadyToSend_FalseBeforeHoldElapses()
    {
        var msg = Queued(holdDuration: TimeSpan.FromMinutes(10));
        Assert.False(msg.IsReadyToSend(T0.AddMinutes(9)));
        Assert.False(msg.IsReadyToSend(T0.AddSeconds(599)));
    }

    [Fact]
    public void IsReadyToSend_TrueAtAndAfterHoldBoundary()
    {
        var msg = Queued(holdDuration: TimeSpan.FromMinutes(10));
        Assert.True(msg.IsReadyToSend(T0.AddMinutes(10)));   // boundary
        Assert.True(msg.IsReadyToSend(T0.AddMinutes(11)));   // after
    }

    [Fact]
    public void IsReadyToSend_FalseOnceSent()
    {
        var msg = Queued(holdDuration: TimeSpan.FromMinutes(10));
        msg.MarkSent(T0.AddMinutes(11));
        Assert.False(msg.IsReadyToSend(T0.AddMinutes(12)));
    }

    [Fact]
    public void IsReadyToSend_FalseOnceCancelled()
    {
        var msg = Queued(holdDuration: TimeSpan.FromMinutes(10));
        msg.Cancel();
        Assert.False(msg.IsReadyToSend(T0.AddMinutes(20)));
    }

    // ───────────────────────────────────────────────── MarkSent ─────────

    [Fact]
    public void MarkSent_FlipsToSent_AndStampsSentAt()
    {
        var msg = Queued(holdDuration: TimeSpan.FromMinutes(10));
        var sentAt = T0.AddMinutes(11);

        msg.MarkSent(sentAt);

        Assert.Equal(OutboundMessageStatus.Sent, msg.Status);
        Assert.Equal(sentAt, msg.SentAt);
    }

    [Fact]
    public void MarkSent_RefusesBeforeHoldElapses()
    {
        // Defense-in-depth on the dispatcher's SELECT predicate. A buggy
        // dispatcher that ignores held_until still gets refused here.
        var msg = Queued(holdDuration: TimeSpan.FromMinutes(10));
        var ex = Assert.Throws<InvalidOperationException>(
            () => msg.MarkSent(T0.AddMinutes(5)));
        Assert.Contains("hold window", ex.Message);
    }

    [Fact]
    public void MarkSent_RefusesAfterSent()
    {
        var msg = Queued();
        msg.MarkSent(T0.AddMinutes(11));
        Assert.Throws<InvalidOperationException>(
            () => msg.MarkSent(T0.AddMinutes(12)));
    }

    [Fact]
    public void MarkSent_RefusesAfterCancel()
    {
        // The yank-then-send race: cancelled message must not be revived
        // by a dispatcher that already SELECTed it before the cancel
        // committed.
        var msg = Queued();
        msg.Cancel();
        Assert.Throws<InvalidOperationException>(
            () => msg.MarkSent(T0.AddMinutes(11)));
    }

    // ───────────────────────────────────────────────── Cancel ───────────

    [Fact]
    public void Cancel_FlipsQueuedToCancelled()
    {
        var msg = Queued();
        msg.Cancel();
        Assert.Equal(OutboundMessageStatus.Cancelled, msg.Status);
        Assert.Null(msg.SentAt);
    }

    [Fact]
    public void Cancel_RefusesAfterSent()
    {
        var msg = Queued();
        msg.MarkSent(T0.AddMinutes(11));
        Assert.Throws<InvalidOperationException>(() => msg.Cancel());
    }

    [Fact]
    public void Cancel_RefusesDoubleCancel()
    {
        var msg = Queued();
        msg.Cancel();
        Assert.Throws<InvalidOperationException>(() => msg.Cancel());
    }

    private static OutboundMessage Queued(TimeSpan? holdDuration = null) =>
        OutboundMessage.Compose(
            ProviderId, TurnId, MessageKind.Followup,
            toAddress: ToAddr,
            subject: "subj", body: "body",
            composedAt: T0,
            holdDuration: holdDuration);
}
