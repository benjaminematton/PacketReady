using PacketReady.Domain.Scoring;

namespace PacketReady.Application.Providers.Aggregation;

/// <summary>
/// Validator-side ergonomic for building a <see cref="Citation"/> from a
/// <see cref="FieldProvenance"/> lookup. <see cref="Cite"/> returns a citation
/// with <see cref="Citation.DocumentId"/>/<see cref="Citation.Page"/>/<see cref="Citation.Bbox"/>
/// populated when the key resolves and all-null when it doesn't —
/// validators don't need to branch on hit/miss, the citation carries the right
/// shape either way. Validators that need to react to a miss (e.g. log a
/// schema-drift warning) can use <see cref="TryCite"/> instead.
/// </summary>
public static class ProvenanceExtensions
{
    public static Citation Cite(
        this IReadOnlyDictionary<string, FieldProvenance> provenance,
        string sourceValidator,
        string extractedValue,
        string provenanceKey)
    {
        TryCite(provenance, sourceValidator, extractedValue, provenanceKey, out var citation);
        return citation;
    }

    /// <summary>
    /// Builds the same citation as <see cref="Cite"/> and additionally reports
    /// whether <paramref name="provenanceKey"/> resolved against the map.
    /// Returns <c>true</c> on hit (doc-ref fields populated), <c>false</c> on
    /// miss (doc-ref fields null) — useful for validators that want to flag
    /// the gap rather than silently render a citation without a PDF link.
    /// </summary>
    public static bool TryCite(
        this IReadOnlyDictionary<string, FieldProvenance> provenance,
        string sourceValidator,
        string extractedValue,
        string provenanceKey,
        out Citation citation)
    {
        var hit = provenance.TryGetValue(provenanceKey, out var prov);
        citation = new Citation(
            SourceValidator: sourceValidator,
            ExtractedValue: extractedValue,
            DocumentId: prov?.DocumentId,
            Page: prov?.Page,
            Bbox: prov?.Bbox);
        return hit;
    }
}
