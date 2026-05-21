using PacketReady.Domain.Audit;
using Xunit;

namespace PacketReady.Tests.Domain;

public class AuditEventTests
{
    [Fact]
    public void Create_RejectsEmptyEventType()
    {
        var ex = Assert.Throws<ArgumentException>(() => AuditEvent.Create("", "{}"));
        Assert.Equal("eventType", ex.ParamName);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{unterminated")]
    [InlineData("{\"x\": }")]
    public void Create_RejectsInvalidJsonPayload(string badJson)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => AuditEvent.Create(AuditEventType.PingExecuted, badJson));
        Assert.Equal("payloadJson", ex.ParamName);
    }

    [Fact]
    public void Create_TreatsEmptyPayloadAsEmptyObject()
    {
        var evt = AuditEvent.Create(AuditEventType.PingExecuted, "");
        Assert.Equal("{}", evt.Payload);
    }

    [Fact]
    public void Create_PopulatesIdAndTimestamp()
    {
        var before = DateTimeOffset.UtcNow;
        var evt = AuditEvent.Create(AuditEventType.PingExecuted, "{\"x\":1}");
        var after = DateTimeOffset.UtcNow;

        Assert.NotEqual(Guid.Empty, evt.Id);
        Assert.InRange(evt.OccurredAt, before, after);
        Assert.Equal(AuditEventType.PingExecuted, evt.EventType);
        Assert.Equal("{\"x\":1}", evt.Payload);
    }

    [Fact]
    public void Create_PreservesOptionalIds()
    {
        var providerId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();

        var evt = AuditEvent.Create(
            AuditEventType.PingExecuted,
            "{}",
            providerId: providerId,
            turnId: turnId,
            correlationId: correlationId);

        Assert.Equal(providerId, evt.ProviderId);
        Assert.Equal(turnId, evt.TurnId);
        Assert.Equal(correlationId, evt.CorrelationId);
    }
}
