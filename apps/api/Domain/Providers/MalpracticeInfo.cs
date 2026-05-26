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
/// <para><see cref="PerOccurrence"/> and <see cref="Aggregate"/> are policy
/// coverage limits in whole dollars (e.g. <c>1_000_000</c> for $1M). Populated
/// by the malpractice extractor (prompt v2). Nullable because some
/// certificates print only an aggregate, some only a per-occurrence cap, and
/// pre-v2 extractions left both unset. The P4
/// <c>MalpracticeCurrencyValidator</c> emits Major only when the extracted
/// value is non-null and below the payer minimum; a null value means
/// "extractor couldn't read this number", which is the aggregator's lane
/// (Partial-Extraction Critical), not the currency validator's.</para>
///
/// <para>Default-empty <see cref="FullName"/> and null coverage fields keep
/// the record source-compatible with pre-P4 fixtures and pre-v2 extraction
/// rows.</para>
/// </summary>
public sealed record MalpracticeInfo(
    string Carrier,
    string PolicyNumber,
    DateOnly ExpiryDate,
    MalpracticeStatus Status,
    string FullName = "",
    long? PerOccurrence = null,
    long? Aggregate = null);

public enum MalpracticeStatus { Unknown = 0, Active = 1, Lapsed = 2, Cancelled = 3 }
