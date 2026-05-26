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
/// </summary>
public sealed record LicenseInfo(
    string Number,
    string State,
    DateOnly IssueDate,
    DateOnly ExpiryDate,
    LicenseStatus Status,
    string FullName = "");

public enum LicenseStatus { Unknown = 0, Active = 1, Suspended = 2, Expired = 3 }
