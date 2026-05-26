namespace PacketReady.Domain.Providers;

/// <summary>
/// Per-document data extracted from a state medical license.
///
/// <para><see cref="FullName"/> is the literal name printed on the license
/// PDF, threaded through verbatim. <see cref="ProviderProfile.FullName"/> is
/// the aggregated/canonical choice (license wins ties); this field is the
/// per-doc value the LLM cross-doc identity validator reads. Default
/// <c>""</c> is the trailing-optional escape hatch for pre-P4 callers
/// (Test/Seed fixtures that pre-date the field).</para>
///
/// <para><see cref="TaxonomyCode"/> is the NUCC taxonomy code printed on
/// the license (P4 task 10 — license extractor v2). Used by
/// <c>NpiTaxonomyMatchValidator</c>: the deterministic NUCC lookup maps
/// the code to a canonical specialty, then a thin LLM call compares it
/// against <see cref="BoardCertInfo.Specialty"/>. Default <c>""</c>
/// matches the sibling fields' pattern; the validator short-circuits
/// when the code is blank (no signal to act on).</para>
/// </summary>
public sealed record LicenseInfo(
    string Number,
    string State,
    DateOnly IssueDate,
    DateOnly ExpiryDate,
    LicenseStatus Status,
    string FullName = "",
    string TaxonomyCode = "");

public enum LicenseStatus { Unknown = 0, Active = 1, Suspended = 2, Expired = 3 }
