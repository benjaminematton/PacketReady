using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PacketReady.Application.Abstractions;
using PacketReady.Application.Intake.MagicLinks;
using PacketReady.Domain.MagicLinks;

namespace PacketReady.Infrastructure.MagicLinks;

/// <summary>
/// HMAC-SHA256 token signer + DB-backed validator. Implements
/// <see cref="IMagicLinkAuthority"/>.
///
/// <para><b>Token shape.</b> <c>&lt;base64url(link-id-bytes)&gt;.&lt;base64url(hmac)&gt;</c>
/// where <c>link-id-bytes</c> is the 16-byte big-endian representation of
/// the <see cref="MagicLink.Id"/> Guid, and <c>hmac</c> is the 32-byte
/// HMAC-SHA256 of those id bytes under the configured signing key. No JWT
/// header, no claims payload, no exp embedded — expiry lives in the
/// <c>magic_links</c> row, not in the token. Saves ~100 bytes per URL.</para>
///
/// <para><b>Why not a JWT package.</b> The token only authenticates the row
/// id; everything else (expiry, consumed state, provider linkage) is in
/// the DB row. JWT's claims model is overkill, and an additional NuGet dep
/// for a 30-line HMAC is more rope than save.</para>
/// </summary>
public sealed class MagicLinkIssuer : IMagicLinkAuthority
{
    private readonly IAppDbContext _db;
    private readonly byte[] _signingKey;

    // 32 bytes = 256 bits, the HMAC-SHA256 block size. Shorter keys halve
    // the security of the MAC and offer no operational benefit. Enforced
    // at construction so a misconfigured deploy refuses to start (matches
    // the DB_CONNECTION_STRING / ANTHROPIC_API_KEY fail-loud pattern).
    private const int MinSigningKeyBytes = 32;

    public MagicLinkIssuer(IAppDbContext db, MagicLinkOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SigningKey))
            throw new InvalidOperationException(
                "MAGIC_LINK_SIGNING_KEY is required; configure it before app start.");

        var keyBytes = Encoding.UTF8.GetBytes(options.SigningKey);
        if (keyBytes.Length < MinSigningKeyBytes)
            throw new InvalidOperationException(
                $"MAGIC_LINK_SIGNING_KEY must be at least {MinSigningKeyBytes} bytes (got {keyBytes.Length}); " +
                "generate one with `openssl rand -base64 32` or equivalent.");

        _db = db;
        _signingKey = keyBytes;
    }

    public string SignToken(MagicLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (link.Id == Guid.Empty)
            throw new ArgumentException("Magic link id must be non-empty.", nameof(link));

        var idBytes = link.Id.ToByteArray();
        var hmac = HMACSHA256.HashData(_signingKey, idBytes);

        return Base64Url(idBytes) + "." + Base64Url(hmac);
    }

    public async Task<MagicLink> ValidateAsync(
        string token,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new MagicLinkInvalidException(MagicLinkInvalidReason.Malformed,
                "Token is empty.");

        var dot = token.IndexOf('.');
        if (dot <= 0 || dot == token.Length - 1)
            throw new MagicLinkInvalidException(MagicLinkInvalidReason.Malformed,
                "Token must be <id>.<sig>.");

        byte[] idBytes, sigBytes;
        try
        {
            idBytes = FromBase64Url(token.AsSpan(0, dot));
            sigBytes = FromBase64Url(token.AsSpan(dot + 1));
        }
        catch (FormatException)
        {
            throw new MagicLinkInvalidException(MagicLinkInvalidReason.Malformed,
                "Token segments are not base64url.");
        }

        if (idBytes.Length != 16)
            throw new MagicLinkInvalidException(MagicLinkInvalidReason.Malformed,
                $"Expected 16-byte id, got {idBytes.Length}.");
        if (sigBytes.Length != 32)
            throw new MagicLinkInvalidException(MagicLinkInvalidReason.Malformed,
                $"Expected 32-byte signature, got {sigBytes.Length}.");

        var expectedSig = HMACSHA256.HashData(_signingKey, idBytes);
        // Constant-time compare: a naïve SequenceEqual leaks the first
        // differing byte's position via timing, which over millions of
        // attempts narrows the search space. CryptographicOperations is the
        // stdlib's hardened compare.
        if (!CryptographicOperations.FixedTimeEquals(expectedSig, sigBytes))
            throw new MagicLinkInvalidException(MagicLinkInvalidReason.BadSignature);

        var linkId = new Guid(idBytes);

        var link = await _db.MagicLinks
            .AsTracking()
            .SingleOrDefaultAsync(l => l.Id == linkId, ct);
        if (link is null)
            throw new MagicLinkInvalidException(MagicLinkInvalidReason.NotFound,
                $"No magic_links row with id {linkId}.");

        // Order matters: Consumed beats Expired in the message because a
        // consumed-then-expired link should signal "you used this" rather
        // than "this is too old."
        if (link.ConsumedAt is not null)
            throw new MagicLinkInvalidException(MagicLinkInvalidReason.Consumed,
                $"Link {linkId} consumed at {link.ConsumedAt:o}.");

        if (now >= link.ExpiresAt)
            throw new MagicLinkInvalidException(MagicLinkInvalidReason.Expired,
                $"Link {linkId} expired at {link.ExpiresAt:o}.");

        return link;
    }

    // RFC 4648 §5: base64url with no padding. Three-char `==` saved per
    // 16-byte segment; a typical magic link URL is shorter end-to-end.
    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] FromBase64Url(ReadOnlySpan<char> s)
    {
        // Re-pad to a multiple of 4 and undo the url-safe substitutions.
        var padded = new string(s).Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
            case 0: break;
            default:
                // 1 mod 4 is impossible for a well-formed base64 segment.
                throw new FormatException("Bad base64url length.");
        }
        return Convert.FromBase64String(padded);
    }
}
