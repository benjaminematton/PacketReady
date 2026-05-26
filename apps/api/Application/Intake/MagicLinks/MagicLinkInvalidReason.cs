namespace PacketReady.Application.Intake.MagicLinks;

/// <summary>
/// Why a magic-link token failed validation. Surfaced in
/// <see cref="MagicLinkInvalidException"/> and mapped to <c>410 Gone</c> via
/// <c>ProblemResults.MagicLinkInvalid</c>. The wire-shape string for each
/// is the enum member name verbatim — kept stable so the portal Next.js
/// page can branch on it.
/// </summary>
public enum MagicLinkInvalidReason
{
    /// <summary>Token shape didn't parse (bad base64, missing segment, malformed id).</summary>
    Malformed,

    /// <summary>HMAC signature didn't match — caller forged or mutated the token.</summary>
    BadSignature,

    /// <summary>Signature checks out but no magic_links row has this id (re-issued / wiped).</summary>
    NotFound,

    /// <summary>Link row exists but <c>expires_at &lt;= now</c>.</summary>
    Expired,

    /// <summary>Link row exists but <c>consumed_at IS NOT NULL</c> — already used.</summary>
    Consumed,
}
