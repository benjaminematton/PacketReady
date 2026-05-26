namespace PacketReady.Infrastructure.Outbox;

/// <summary>
/// Configuration for <see cref="MockSmtpSender"/>. Bound from
/// <c>MOCK_SMTP_ROOT</c> at startup (env var or appsettings); defaults to
/// <c>&lt;api-content-root&gt;/outbox</c> when unset so a fresh dev box
/// produces inspectable <c>.eml</c> files without explicit configuration.
/// </summary>
public sealed class MockSmtpOptions
{
    /// <summary>
    /// Absolute directory path for the outbox tree. <see cref="MockSmtpSender"/>
    /// writes to <c>{RootPath}/sent/{yyyy-MM-dd}/{messageId}.eml</c>; the date
    /// shard keeps a single directory from accumulating thousands of files
    /// over a long demo loop and makes "what did we send today?" a one-shell
    /// <c>ls</c>.
    /// </summary>
    public string RootPath { get; init; } = string.Empty;
}
