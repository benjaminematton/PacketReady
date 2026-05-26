using System.Text;
using Microsoft.Extensions.Logging;
using PacketReady.Application.Intake.Outbox;

namespace PacketReady.Infrastructure.Outbox;

/// <summary>
/// File-writing <see cref="IEmailSender"/> for the P5 demo loop. Every
/// dispatched message lands as <c>{RootPath}/sent/{yyyy-MM-dd}/{id}.eml</c>
/// in RFC 5322 format, plus a one-line stdout breadcrumb so the demo
/// terminal shows the dispatch without a separate log tail.
///
/// <para>Real SMTP is out of scope (phase-5-intake-agent.md, "Out of
/// scope"). The dispatch layer is what's load-bearing for the lifecycle;
/// the transport is a stub. A real impl slots in via DI replacement when
/// (and only when) the demo needs to email a real Atano reviewer.</para>
/// </summary>
public sealed class MockSmtpSender : IEmailSender
{
    private readonly MockSmtpOptions _options;
    private readonly ILogger<MockSmtpSender> _logger;
    private readonly string _root;

    public MockSmtpSender(MockSmtpOptions options, ILogger<MockSmtpSender> logger)
    {
        if (string.IsNullOrWhiteSpace(options.RootPath))
            throw new ArgumentException("Mock SMTP root path is required.", nameof(options));

        _options = options;
        _logger = logger;

        // Canonicalize once at construction (same pattern as LocalFileBlobStore):
        // trailing-separator + relative-path resolution happens here, not on
        // every send.
        _root = Path.GetFullPath(_options.RootPath);
        Directory.CreateDirectory(_root);
    }

    public async Task SendAsync(EmailEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ValidateHeaderSafety(envelope);

        // UtcDateTime collapses any non-UTC offset to UTC for the date shard
        // — "what day did we send?" is a UTC question; a 23:30 PST send and
        // a 08:30 next-day PST send shouldn't land in different folders.
        var dateDir = envelope.Date.UtcDateTime.ToString("yyyy-MM-dd");
        var dir = Path.Combine(_root, "sent", dateDir);
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, $"{envelope.MessageId:D}.eml");
        var content = FormatEml(envelope);

        // CreateNew: a re-dispatch of the same MessageId on the same day
        // throws IOException. The dispatcher's outbox dedup UNIQUE catches
        // this upstream, but failing loud here surfaces a misrouted retry
        // immediately instead of overwriting prior dispatch evidence.
        await using (var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true))
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            await stream.WriteAsync(bytes, ct);
        }

        // Stdout breadcrumb — explicit Console.Out rather than ILogger so a
        // demo running with `Logging:LogLevel:Default:Warning` still shows
        // the dispatch line. ILogger fires too for structured downstream.
        Console.Out.WriteLine(
            $"[mock-smtp] sent {envelope.MessageId:D} → {envelope.ToAddress}  ({path})");
        _logger.LogInformation(
            "MockSmtp dispatched message {MessageId} to {ToAddress} at {Path}",
            envelope.MessageId, envelope.ToAddress, path);
    }

    private static string FormatEml(EmailEnvelope envelope)
    {
        // RFC 5322: CRLF line endings, headers then blank line then body.
        // Minimal header set; Date in RFC 1123 GMT format ("r"), which is
        // valid RFC 5322 too. Message-ID uses the OutboundMessage id so an
        // .eml file in the tree maps 1:1 back to a DB row.
        var sb = new StringBuilder();
        sb.Append("From: ").Append(envelope.FromAddress).Append("\r\n");
        sb.Append("To: ").Append(envelope.ToAddress).Append("\r\n");
        sb.Append("Subject: ").Append(envelope.Subject).Append("\r\n");
        sb.Append("Date: ").Append(envelope.Date.ToString("r")).Append("\r\n");
        sb.Append("Message-ID: <")
            .Append(envelope.MessageId.ToString("D"))
            .Append("@packetready.local>\r\n");
        sb.Append("Content-Type: text/plain; charset=UTF-8\r\n");
        sb.Append("\r\n");
        sb.Append(envelope.Body);
        if (!envelope.Body.EndsWith('\n'))
            sb.Append("\r\n");
        return sb.ToString();
    }

    // Header-injection guard. The body is free-form (post the blank line,
    // CRLF has no header meaning), but a CRLF in From/To/Subject would let a
    // malicious value smuggle in additional headers — refuse rather than try
    // to sanitize.
    private static void ValidateHeaderSafety(EmailEnvelope envelope)
    {
        if (envelope.MessageId == Guid.Empty)
            throw new ArgumentException("MessageId is required.", nameof(envelope));

        EnsureNoCrlf(envelope.FromAddress, nameof(envelope.FromAddress));
        EnsureNoCrlf(envelope.ToAddress, nameof(envelope.ToAddress));
        EnsureNoCrlf(envelope.Subject, nameof(envelope.Subject));

        if (string.IsNullOrWhiteSpace(envelope.FromAddress))
            throw new ArgumentException("FromAddress is required.", nameof(envelope));
        if (string.IsNullOrWhiteSpace(envelope.ToAddress))
            throw new ArgumentException("ToAddress is required.", nameof(envelope));
        if (string.IsNullOrWhiteSpace(envelope.Subject))
            throw new ArgumentException("Subject is required.", nameof(envelope));
        if (envelope.Body is null)
            throw new ArgumentException("Body is required.", nameof(envelope));
    }

    private static void EnsureNoCrlf(string value, string fieldName)
    {
        if (value is null) return;
        if (value.IndexOfAny(['\r', '\n']) >= 0)
            throw new ArgumentException(
                $"Header field {fieldName} must not contain CR/LF.",
                nameof(EmailEnvelope));
    }
}
