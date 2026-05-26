using PacketReady.Application.Scoring;
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
///
/// <para><b>Low-confidence stamping (P4):</b> when the resolved
/// <see cref="FieldProvenance.Confidence"/> is below
/// <see cref="ConfidenceGuard.CriticalEligibleThreshold"/>, the citation
/// is built with <see cref="Citation.LowConfidence"/> = <c>true</c>.
/// <see cref="ConfidenceGuard.Apply"/> downstream reads this flag to
/// downgrade Critical Issues whose citations point at low-confidence
/// inputs. Validators that build <see cref="Citation"/> directly (not via
/// these helpers) MUST set the flag themselves, or they bypass the
/// guard — every validator citation in P4 goes through here.</para>
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
            Bbox: prov?.Bbox)
        {
            // Miss → no provenance to score, treat as full-confidence (the
            // citation has null doc-refs anyway; ConfidenceGuard only acts
            // when LowConfidence == true). Hit → stamp based on the
            // extractor's self-reported per-field confidence.
            LowConfidence = hit && prov is not null
                            && prov.Confidence < ConfidenceGuard.CriticalEligibleThreshold,
        };
        return hit;
    }
}
