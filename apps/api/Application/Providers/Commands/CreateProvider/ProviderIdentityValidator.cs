using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PacketReady.Application.Providers.Commands.CreateProvider;

/// <summary>
/// Boundary-side identity validation. Returns the full list of violations,
/// not just the first — clients submitting an off-shape payload see every
/// problem at once. The downstream domain (<c>ProviderProfile.Validate</c>)
/// must never see a malformed identity; if it does, this validator and
/// the domain validate are out of sync.
///
/// <para><b>What this catches that the domain doesn't:</b> NPI Luhn check.
/// <c>ProviderProfile.Validate</c> enforces only the 10-digit regex (the
/// domain's invariant for storage); the Luhn check belongs at the
/// boundary because it's a synthetic-data contract — the eval generator
/// emits Luhn-valid NPIs, the P5 admin intake will validate user input
/// against Luhn, and a stray non-Luhn NPI is operator error worth
/// surfacing immediately.</para>
/// </summary>
public static partial class ProviderIdentityValidator
{
    /// <summary>The earliest date we accept as a DOB. Older than this is
    /// implausible for a credentialing provider and signals a data error.</summary>
    public static readonly DateOnly MinDateOfBirth = new(1900, 1, 2);

    /// <summary>Placeholder identity for callers that don't supply one. All
    /// four fields are Luhn/regex-valid synthetic values; the placeholder
    /// satisfies both this validator and <c>ProviderProfile.Validate</c>.
    /// The type-initializer below verifies this constant on every load —
    /// a typo edit to the literal NPI will fail the assertion the first
    /// time anything touches the type (a Debug build break in CI; tests
    /// would also pin it).</summary>
    public static readonly ProviderIdentityDto Placeholder = new(
        FullName: "[unverified]",
        Npi: "1234567893",
        DateOfBirth: new DateOnly(1980, 1, 1),
        CredentialingState: "XX");

    static ProviderIdentityValidator()
    {
        Debug.Assert(
            IsNpiLuhnValid(Placeholder.Npi),
            "Placeholder NPI must satisfy the CMS Luhn check — edit ProviderIdentityValidator.Placeholder with care.");
    }

    [GeneratedRegex(@"^\d{10}$")]
    private static partial Regex NpiShapeRegex();

    [GeneratedRegex(@"^[A-Z]{2}$")]
    private static partial Regex StateRegex();

    /// <summary>
    /// Validates an identity DTO. Returns an empty list on success;
    /// non-empty list of violations on failure. The endpoint maps a
    /// non-empty list to a 400 carrying all entries.
    /// </summary>
    public static IReadOnlyList<string> Validate(ProviderIdentityDto identity, DateOnly today)
    {
        var errors = new List<string>(capacity: 4);

        if (string.IsNullOrWhiteSpace(identity.FullName))
            errors.Add("fullName must be a non-empty string.");

        if (!NpiShapeRegex().IsMatch(identity.Npi))
            errors.Add($"npi must be exactly 10 digits; got {identity.Npi.Length} character(s).");
        else if (!IsNpiLuhnValid(identity.Npi))
            errors.Add("npi failed the CMS Luhn (mod-10) check digit calculation.");

        if (identity.DateOfBirth < MinDateOfBirth)
            errors.Add(
                $"dateOfBirth must be on or after {MinDateOfBirth:yyyy-MM-dd}; "
                + $"got {identity.DateOfBirth:yyyy-MM-dd}.");
        if (identity.DateOfBirth > today)
            errors.Add(
                $"dateOfBirth must not be in the future; "
                + $"got {identity.DateOfBirth:yyyy-MM-dd} against today {today:yyyy-MM-dd}.");

        // StateRegex().IsMatch returns false for null/empty; we only branch
        // on null to keep the error message from interpolating "got 'null'".
        if (identity.CredentialingState is null || !StateRegex().IsMatch(identity.CredentialingState))
            errors.Add(
                "credentialingState must be 2 uppercase letters (e.g. \"NY\"); "
                + $"got '{identity.CredentialingState ?? "(null)"}'.");

        return errors;
    }

    /// <summary>
    /// CMS NPI Luhn validation. The 10-digit NPI is prefixed with the
    /// ISO 7812 issuer-identifier "80840" to produce a 15-digit string;
    /// standard Luhn-mod-10 over that string must sum to 0 mod 10.
    /// Returns false on non-digit input.
    /// </summary>
    public static bool IsNpiLuhnValid(string npi)
    {
        if (npi is null || npi.Length != 10) return false;
        for (var i = 0; i < npi.Length; i++)
            if (npi[i] < '0' || npi[i] > '9') return false;

        // Walk "80840" + npi from the right; double every second digit.
        // Hard-code the prefix scan instead of allocating a 15-char string.
        ReadOnlySpan<char> prefix = "80840";
        var sum = 0;
        var totalLen = prefix.Length + npi.Length;       // 15
        for (var i = 0; i < totalLen; i++)
        {
            // i counted from the rightmost (position 0 = check digit).
            var idxFromLeft = totalLen - 1 - i;
            var d = idxFromLeft < prefix.Length
                ? prefix[idxFromLeft] - '0'
                : npi[idxFromLeft - prefix.Length] - '0';
            if ((i & 1) == 1)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
        }
        return sum % 10 == 0;
    }
}
