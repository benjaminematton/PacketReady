using Microsoft.Extensions.Logging.Abstractions;
using PacketReady.Application.Intake.Outbox;
using PacketReady.Infrastructure.Outbox;
using Xunit;

namespace PacketReady.Tests.Infrastructure.Outbox;

// Console.SetOut is process-global. The collection tag opts this class out
// of xUnit's cross-class parallelism so a second class that ever swaps
// Console.Out can join the "ConsoleOut" collection and serialize against
// this one without cross-test contamination.
[Collection("ConsoleOut")]
public class MockSmtpSenderTests : IDisposable
{
    private readonly string _root;
    private readonly MockSmtpSender _sender;
    private readonly TextWriter _originalStdout;
    private readonly StringWriter _capturedStdout;

    public MockSmtpSenderTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "packetready-mocksmtp-tests-" + Guid.NewGuid().ToString("N"));
        _sender = new MockSmtpSender(
            new MockSmtpOptions { RootPath = _root },
            NullLogger<MockSmtpSender>.Instance);

        _originalStdout = Console.Out;
        _capturedStdout = new StringWriter();
        Console.SetOut(_capturedStdout);
    }

    public void Dispose()
    {
        Console.SetOut(_originalStdout);
        _capturedStdout.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static EmailEnvelope MakeEnvelope(
        Guid? messageId = null,
        DateTimeOffset? date = null,
        string subject = "test subject",
        string body = "test body") => new(
            MessageId: messageId ?? Guid.NewGuid(),
            ToAddress: "provider@example.com",
            FromAddress: "noreply@packetready.local",
            Subject: subject,
            Body: body,
            Date: date ?? new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));

    // ────────────────────────────────────────────── happy path ──────────

    [Fact]
    public async Task SendAsync_WritesEmlUnderSentDateShard()
    {
        var id = Guid.NewGuid();
        var env = MakeEnvelope(messageId: id);

        await _sender.SendAsync(env);

        var expected = Path.Combine(_root, "sent", "2026-06-01", $"{id:D}.eml");
        Assert.True(File.Exists(expected), $"Expected .eml at {expected}");
    }

    [Fact]
    public async Task SendAsync_DateShardUsesUtc()
    {
        // 23:30 PDT (UTC-7) → 06:30 UTC next day. The shard must come from
        // the UTC date, not the local one, so a late-evening dispatch and
        // an early-morning one don't land in different folders.
        var date = new DateTimeOffset(2026, 6, 1, 23, 30, 0, TimeSpan.FromHours(-7));
        var env = MakeEnvelope(date: date);

        await _sender.SendAsync(env);

        var utcShard = Path.Combine(_root, "sent", "2026-06-02");
        Assert.True(Directory.Exists(utcShard));
        Assert.Single(Directory.GetFiles(utcShard));
    }

    [Fact]
    public async Task SendAsync_EmlContainsExpectedHeadersAndBody()
    {
        var id = Guid.NewGuid();
        var env = MakeEnvelope(
            messageId: id,
            subject: "please upload your DEA",
            body: "we still need the DEA expiration date.");

        await _sender.SendAsync(env);

        var path = Path.Combine(_root, "sent", "2026-06-01", $"{id:D}.eml");
        var content = await File.ReadAllTextAsync(path);

        Assert.Contains("From: noreply@packetready.local\r\n", content);
        Assert.Contains("To: provider@example.com\r\n", content);
        Assert.Contains("Subject: please upload your DEA\r\n", content);
        Assert.Contains($"Message-ID: <{id:D}@packetready.local>\r\n", content);
        Assert.Contains("Content-Type: text/plain; charset=UTF-8\r\n", content);
        Assert.Contains("we still need the DEA expiration date.", content);

        // Headers/body separated by a blank CRLF line per RFC 5322.
        Assert.Contains("\r\n\r\n", content);
    }

    [Fact]
    public async Task SendAsync_WritesStdoutBreadcrumb()
    {
        var id = Guid.NewGuid();
        var env = MakeEnvelope(messageId: id);

        await _sender.SendAsync(env);

        var stdout = _capturedStdout.ToString();
        Assert.Contains("[mock-smtp]", stdout);
        Assert.Contains(id.ToString("D"), stdout);
        Assert.Contains("provider@example.com", stdout);
    }

    // ────────────────────────────────────────── header injection ────────

    [Theory]
    [InlineData("evil\r\nBcc: attacker@evil.com")]
    [InlineData("evil\nX-Injected: 1")]
    public async Task SendAsync_RejectsCrlfInSubject(string subject)
    {
        var env = MakeEnvelope(subject: subject);
        await Assert.ThrowsAsync<ArgumentException>(() => _sender.SendAsync(env));
    }

    [Fact]
    public async Task SendAsync_RejectsCrlfInToAddress()
    {
        var env = MakeEnvelope() with { ToAddress = "ok@example.com\r\nBcc: x" };
        await Assert.ThrowsAsync<ArgumentException>(() => _sender.SendAsync(env));
    }

    [Fact]
    public async Task SendAsync_RejectsCrlfInFromAddress()
    {
        var env = MakeEnvelope() with { FromAddress = "ok@example.com\nX-Smuggled: 1" };
        await Assert.ThrowsAsync<ArgumentException>(() => _sender.SendAsync(env));
    }

    [Fact]
    public async Task SendAsync_RejectsEmptyMessageId()
    {
        var env = MakeEnvelope(messageId: Guid.Empty);
        await Assert.ThrowsAsync<ArgumentException>(() => _sender.SendAsync(env));
    }

    // ──────────────────────────────────────── re-dispatch guard ─────────

    [Fact]
    public async Task SendAsync_RefusesDuplicateMessageIdOnSameDay()
    {
        // CreateNew: a misrouted retry surfaces immediately as an IOException
        // rather than overwriting prior dispatch evidence.
        var id = Guid.NewGuid();
        var env = MakeEnvelope(messageId: id);

        await _sender.SendAsync(env);
        await Assert.ThrowsAsync<IOException>(() => _sender.SendAsync(env));
    }
}
