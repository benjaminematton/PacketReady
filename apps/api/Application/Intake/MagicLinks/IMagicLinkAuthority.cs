using PacketReady.Domain.MagicLinks;

namespace PacketReady.Application.Intake.MagicLinks;

/// <summary>
/// Port for magic-link token signing + validation. The Infrastructure impl
/// (<c>MagicLinkIssuer</c>) holds the HMAC secret + a scoped
/// <c>IAppDbContext</c> for the row lookup.
///
/// <para>The token shape is <c>&lt;base64url(link-id)&gt;.&lt;base64url(hmac)&gt;</c>
/// — minimum sufficient: the row carries expiry + consumed state, so the
/// token only needs to authenticate the row id. No JWT package dep.</para>
/// </summary>
public interface IMagicLinkAuthority
{
    /// <summary>
    /// Sign a token for an already-issued <see cref="MagicLink"/>. Pure
    /// crypto; the caller is responsible for having staged the row in the
    /// DbContext. Idempotent — same link → same token.
    /// </summary>
    string SignToken(MagicLink link);

    /// <summary>
    /// Verify token signature, look up the row, check expiry + single-use.
    /// Throws <see cref="MagicLinkInvalidException"/> on any failure with a
    /// <see cref="MagicLinkInvalidReason"/> the endpoint maps to <c>410 Gone</c>.
    /// Does <b>not</b> consume the link — the portal submit handler calls
    /// <see cref="MagicLink.Consume"/> + <c>SaveChanges</c> separately.
    ///
    /// <para><b>Tracking contract.</b> The returned entity IS tracked by the
    /// scoped <c>IAppDbContext</c>. That's deliberate: the portal submit
    /// path mutates (<c>Consume</c>) and saves on the same scope, so a
    /// second roundtrip would be wasted. Callers that only need to read
    /// (e.g. the portal GET) can ignore the tracking — entities go away with
    /// the scope at end-of-request. Do not pass the returned entity to a
    /// different <c>DbContext</c>.</para>
    /// </summary>
    Task<MagicLink> ValidateAsync(
        string token,
        DateTimeOffset now,
        CancellationToken ct = default);
}
