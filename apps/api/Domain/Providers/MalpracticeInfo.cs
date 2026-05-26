namespace PacketReady.Domain.Providers;

/// <summary>
/// Per-document data extracted from a malpractice insurance certificate.
///
/// <para><see cref="FullName"/> is the literal insured-name printed on the
/// malpractice PDF. The P4 IdentityCoherence validator reads it as a fourth
/// source alongside license / DEA / board cert; planted divergences are
/// expressed against this field (a married/hyphenated name on malpractice
/// that doesn't match the license is the canonical real-world case).</para>
///
/// <para>P4.5 will add a malpractice-currency validator that reads
/// <see cref="ExpiryDate"/> + <see cref="Status"/> against the credentialing
/// window — fields are present now so the schema doesn't churn on that ship.
/// Default-empty <see cref="FullName"/> matches the sibling sub-records'
/// pattern for pre-P4 fixtures that pre-date the field.</para>
/// </summary>
public sealed record MalpracticeInfo(
    string Carrier,
    string PolicyNumber,
    DateOnly ExpiryDate,
    MalpracticeStatus Status,
    string FullName = "");

public enum MalpracticeStatus { Unknown = 0, Active = 1, Lapsed = 2, Cancelled = 3 }
