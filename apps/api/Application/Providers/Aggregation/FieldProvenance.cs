using PacketReady.Domain.Scoring;

namespace PacketReady.Application.Providers.Aggregation;

/// <summary>
/// Per-field source-of-truth pointer the aggregator builds for every field it
/// populates on <see cref="Domain.Providers.ProviderProfile"/>. Validators look
/// up <c>"&lt;docType&gt;.&lt;fieldName&gt;"</c> (e.g. <c>"license.expiryDate"</c>)
/// and attach the result to every <see cref="Citation"/> they emit — that's the
/// load-bearing wiring for the dashboard's "click an Issue → see the source PDF
/// at the right page with a bbox highlight" UX.
///
/// <para><see cref="Confidence"/> is the extractor's self-reported confidence
/// for this specific field (not the classifier's doc-type confidence). P4's
/// gate downgrades a Critical Issue to a Minor when any cited field carries
/// confidence &lt; 0.85; P3 plumbs the value through without acting on it.</para>
/// </summary>
public sealed record FieldProvenance(
    Guid DocumentId,
    int Page,
    BoundingBox Bbox,
    double Confidence);
