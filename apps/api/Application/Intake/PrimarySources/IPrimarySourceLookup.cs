using System.Text.Json;

namespace PacketReady.Application.Intake.PrimarySources;

/// <summary>
/// Port for external primary-source verification (NPPES, OIG, SAM, state
/// boards, CAQH). The Infrastructure impl in P5 is
/// <c>MockPrimarySourceLookup</c> — a 5-entry canned table covering the
/// 3 demo providers plus 2 edge cases. Real PSV is post-launch.
///
/// <para><b>Replay safety</b> (design.md §7.9): the runtime caches results
/// in <c>primary_source_results</c> keyed by <c>(source, identifiers_hash)</c>.
/// Re-running a lookup on rewound state hits the cache; only a changed
/// identifier set triggers a fresh outbound call. C4 mocks the source, so
/// the cache is a future concern — but the contract here is shaped to
/// support it (no side effects on the mock; caller controls when to cache).</para>
/// </summary>
public interface IPrimarySourceLookup
{
    /// <summary>
    /// Look the provider up in the named source. <paramref name="identifiers"/>
    /// is a JSON object — typical contents are <c>npi</c>,
    /// <c>license_number</c>, <c>state</c>; per-source-specific.
    /// </summary>
    /// <returns>
    /// A <see cref="JsonElement"/> result with shape
    /// <c>{ found, fields, mismatch_fields }</c> matching design.md §7.4.
    /// On any "we couldn't reach the source" condition the mock returns
    /// <c>{ found: false, error: "..." }</c>.
    /// </returns>
    Task<JsonElement> LookupAsync(
        string source,
        JsonElement identifiers,
        CancellationToken ct);
}
