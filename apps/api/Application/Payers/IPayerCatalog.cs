namespace PacketReady.Application.Payers;

/// <summary>
/// Typed read-only seam over the per-payer requirement table. Resolves a
/// <c>Provider.PayerId</c> to its <see cref="PayerRequirement"/> and fails
/// loud (via <see cref="PayerNotConfiguredException"/>) on an unknown id —
/// previous code threaded a raw <c>IReadOnlyDictionary&lt;string, PayerRequirement&gt;</c>
/// through DI and reimplemented the same "unknown payer" message + Keys
/// dump at four call sites.
///
/// <para>Implementations are expected to be thread-safe and immutable after
/// construction (the catalog is a DI singleton built once at startup).
/// Hot-reload / live-edit support is out of scope; a YAML change is a
/// redeploy.</para>
/// </summary>
public interface IPayerCatalog
{
    /// <summary>
    /// Resolve a payer requirement by id. Throws
    /// <see cref="PayerNotConfiguredException"/> when the id has no backing
    /// YAML — the dedicated exception type lets API middleware map this to a
    /// 4xx, instead of letting a raw
    /// <see cref="KeyNotFoundException"/> from a dictionary lookup surface as
    /// an opaque 500.
    /// </summary>
    PayerRequirement Get(string id);

    /// <summary>
    /// Non-throwing variant. Returns <c>true</c> and populates
    /// <paramref name="requirement"/> on hit; returns <c>false</c> on miss.
    /// </summary>
    bool TryGet(string id, out PayerRequirement requirement);

    /// <summary>Enumerable view over the configured payer ids.</summary>
    IReadOnlyCollection<string> Ids { get; }
}
