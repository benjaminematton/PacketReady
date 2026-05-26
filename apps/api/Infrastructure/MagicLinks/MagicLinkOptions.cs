namespace PacketReady.Infrastructure.MagicLinks;

/// <summary>
/// Configuration for <see cref="MagicLinkIssuer"/>. Bound from
/// <c>MAGIC_LINK_SIGNING_KEY</c> at startup. Must be set in every
/// environment — the issuer fails-loud at DI bootstrap if it's missing,
/// matching the <c>DB_CONNECTION_STRING</c> / <c>ANTHROPIC_API_KEY</c>
/// fail-loud pattern in <c>Infrastructure.DependencyInjection</c>.
///
/// <para>Rotating the signing key invalidates outstanding magic links —
/// noted in phase-5-intake-agent.md "Risks / open: JWT signing-key
/// rotation in dev."</para>
/// </summary>
public sealed class MagicLinkOptions
{
    /// <summary>
    /// HMAC-SHA256 secret. <b>Minimum 32 UTF-8 bytes</b> — the issuer
    /// rejects shorter keys at construction (and rejects an empty key
    /// outright). Generate with <c>openssl rand -base64 32</c> or
    /// equivalent; any key shorter than 32 bytes halves the security of
    /// the MAC and offers no operational benefit.
    /// </summary>
    public string SigningKey { get; init; } = string.Empty;
}
