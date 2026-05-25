using PacketReady.Domain.Documents;

namespace PacketReady.Application.Extraction.Extract;

/// <summary>
/// One LLM-backed field extractor per <see cref="DocType"/>. Concrete impls live in
/// Infrastructure (they need the Anthropic SDK + chat client); the Application
/// layer depends only on this interface so handlers stay LLM-agnostic and Moqable.
///
/// <para>Registered keyed by <see cref="DocType"/>: handlers dispatch via
/// <c>IKeyedServiceProvider.GetRequiredKeyedService&lt;IDocTypeExtractor&gt;(docType)</c>.
/// Spec position (plan §"Positions taken"): one strategy + keyed DI, not four
/// per-type interfaces.</para>
/// </summary>
public interface IDocTypeExtractor
{
    /// <summary>The doc-type this extractor handles. Must match the keyed-DI registration.</summary>
    DocType DocType { get; }

    /// <summary>
    /// Schema-version tag landing on <c>document_extractions.schema_version</c>
    /// (e.g. <c>"license.v1"</c>). Bump when the output shape changes such that
    /// older rows can no longer be deserialized by the aggregator.
    /// </summary>
    string SchemaVersion { get; }

    /// <summary>
    /// Embedded-resource path of the prompt this extractor uses. Resolves via
    /// <c>IPromptLoader</c>; hashes via <c>PromptHasher</c>.
    /// </summary>
    string PromptResourceName { get; }

    /// <summary>
    /// LLM model id landing on <c>document_extractions.model</c>. Part of the
    /// idempotency key alongside <c>prompt_hash</c> — the upload/reextract
    /// handlers need it BEFORE the extractor runs, to short-circuit duplicate
    /// extractions against the cache.
    /// </summary>
    string Model { get; }

    /// <summary>
    /// Extracts fields from <paramref name="pdf"/>. Bytes-in (not Stream-in)
    /// because the Anthropic SDK takes bytes anyway, both call sites have a
    /// fully-materialized buffer in hand (Path A reads the upload to a byte[]
    /// before queueing; Path B pulls from the blob store), and Sonnet has no
    /// streaming surface to forward to. Returns the storage-shape triple plus
    /// LLM provenance for the caller to persist (Path B) or flatten (Path A).
    /// </summary>
    Task<ExtractionResult> ExtractAsync(ReadOnlyMemory<byte> pdf, CancellationToken ct);
}
