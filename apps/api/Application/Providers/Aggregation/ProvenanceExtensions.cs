using PacketReady.Domain.Scoring;

namespace PacketReady.Application.Providers.Aggregation;

/// <summary>
/// Validator-side ergonomic for building a <see cref="Citation"/> from a
/// <see cref="FieldProvenance"/> lookup. Returns a citation with
/// <see cref="Citation.DocumentId"/>/<see cref="Citation.Page"/>/<see cref="Citation.Bbox"/>
/// populated when the key resolves, all-null when it doesn't — validators don't
/// need to branch on hit/miss, the citation carries the right shape either way.
/// </summary>
public static class ProvenanceExtensions
{
    public static Citation Cite(
        this IReadOnlyDictionary<string, FieldProvenance> provenance,
        string sourceValidator,
        string extractedValue,
        string provenanceKey)
    {
        provenance.TryGetValue(provenanceKey, out var prov);
        return new Citation(
            SourceValidator: sourceValidator,
            ExtractedValue: extractedValue,
            DocumentId: prov?.DocumentId,
            Page: prov?.Page,
            Bbox: prov?.Bbox);
    }
}
