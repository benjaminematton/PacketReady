using PacketReady.Application.Providers.Aggregation;
using PacketReady.Application.Scoring.Validators;
using PacketReady.Domain.Providers;
using PacketReady.Domain.Scoring;

namespace PacketReady.Tests.Application.Scoring.Validators;

/// <summary>
/// Test-only ergonomic: <c>validator.RunAsync(profile, ct)</c> in test code
/// implicitly threads an empty provenance map and the default payer id. The
/// P1 validator tests don't assert on Citation.DocumentId/Page/Bbox or on
/// payer config; they only care about Severity + Message + presence/absence.
/// Defaulting here keeps those tests terse without re-touching every call
/// site each time the interface adds a parameter.
///
/// <para>Slice-8 tests that DO care about provenance population call the
/// full interface method directly with a populated dict. P4 payer-aware
/// validator tests likewise pass an explicit payer id.</para>
/// </summary>
internal static class ValidatorTestExtensions
{
    private static readonly IReadOnlyDictionary<string, FieldProvenance> EmptyProvenance =
        new Dictionary<string, FieldProvenance>(StringComparer.Ordinal);

    public static Task<IReadOnlyList<Issue>> RunAsync(
        this IValidator validator,
        ProviderProfile profile,
        CancellationToken ct) =>
        validator.RunAsync(profile, EmptyProvenance, Provider.DefaultPayerId, ct);
}
