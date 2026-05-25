using PacketReady.Application.Providers.Aggregation;
using PacketReady.Application.Scoring.Validators;
using PacketReady.Domain.Providers;
using PacketReady.Domain.Scoring;

namespace PacketReady.Tests.Application.Scoring.Validators;

/// <summary>
/// Test-only ergonomic: <c>validator.RunAsync(profile, ct)</c> in test code
/// implicitly threads an empty provenance map. The P1 validator tests don't
/// assert on Citation.DocumentId/Page/Bbox; they only care about Severity +
/// Message + presence/absence. Wiring an empty dict here keeps those tests
/// terse without re-touching 35 call sites every time the interface adds an
/// optional parameter.
///
/// <para>Slice-8 tests that DO care about provenance population call the
/// three-arg interface method directly with a populated dict.</para>
/// </summary>
internal static class ValidatorTestExtensions
{
    private static readonly IReadOnlyDictionary<string, FieldProvenance> EmptyProvenance =
        new Dictionary<string, FieldProvenance>(StringComparer.Ordinal);

    public static Task<IReadOnlyList<Issue>> RunAsync(
        this IValidator validator,
        ProviderProfile profile,
        CancellationToken ct) =>
        validator.RunAsync(profile, EmptyProvenance, ct);
}
