namespace PacketReady.Domain.Scoring;

/// <summary>
/// Provenance for an <see cref="Issue"/>. Phase 1 carries the validator name and the
/// extracted value the validator reasoned over. Phase 3 will populate
/// <see cref="DocumentId"/>, <see cref="Page"/>, and <see cref="Bbox"/> so the
/// side-panel can highlight the source PDF region.
///
/// <para>The optional doc-ref fields are nullable so the Phase 3 addition is
/// non-breaking — Phase 1 readers ignore them, Phase 3 writers populate them.
/// No versioning column; shape is internal.</para>
/// </summary>
public sealed record Citation(
    string SourceValidator,
    string ExtractedValue,
    Guid? DocumentId = null,
    int? Page = null,
    BoundingBox? Bbox = null)
{
    /// <summary>
    /// Mirror of <see cref="Issue.IsLowConfidenceInput"/> at the citation
    /// level — set when the underlying extracted field had &lt; 0.85
    /// confidence. P3 never flips it; the dashboard can already render
    /// "low-confidence source" badges from the column without further
    /// schema changes once P4 starts populating it.
    /// </summary>
    public bool LowConfidence { get; init; } = false;
}

/// <summary>
/// Axis-aligned bounding box in normalized PDF page coordinates (top-left origin,
/// 0..1 on each axis). Explicit field names avoid the xywh/x1y1x2y2 ambiguity a
/// raw <c>double[]</c> would carry, and the record gives us real value equality.
/// </summary>
public sealed record BoundingBox(double X1, double Y1, double X2, double Y2);
