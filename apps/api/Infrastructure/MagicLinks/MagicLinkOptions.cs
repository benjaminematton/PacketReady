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
    /// HMAC-SHA256 secret. Minimum 32 bytes of entropy when base64-decoded;
    /// shorter keys halve the security of the MAC. The issuer doesn't
    /// enforce length (it does enforce non-empty), so a thin
    /// <c>appsettings</c> value passes — operators are responsible for
    /// shipping a real key in non-dev environments.
    /// </summary>
    public string SigningKey { get; init; } = string.Empty;
}
