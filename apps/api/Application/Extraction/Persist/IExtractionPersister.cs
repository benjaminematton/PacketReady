using PacketReady.Application.Extraction.Extract;
using PacketReady.Domain.Documents;

namespace PacketReady.Application.Extraction.Persist;

/// <summary>
/// Shared body of the upload + reextract handlers: idempotent persistence of
/// one <see cref="DocumentExtraction"/> row against an existing
/// <see cref="Document"/>. Encapsulates the four-step dance from the P3 spec:
///
/// <list type="number">
///   <item>Idempotency pre-check on <c>(documentId, schemaVersion, model, promptHash)</c></item>
///   <item>Run the extractor — or capture its exception as the "Failed" path</item>
///   <item>Open a transaction, take a Postgres advisory lock on the document id,
///   compute <c>MAX(extraction_id) + 1</c>, insert</item>
///   <item>On a <c>23505</c> unique-violation (someone else won the race), re-read
///   the winner and return it as a cache hit</item>
/// </list>
///
/// <para>The LLM call deliberately runs <b>outside</b> the transaction so the
/// connection isn't held for the ~10 s vision call. Losing the race after running
/// the LLM means paying for tokens we discard — that cost is bounded and the
/// alternative (transaction-wrapped LLM call) is worse for pool health.</para>
///
/// <para>Implementation lives in Infrastructure (talks Postgres-specific SQL
/// and catches <c>PostgresException</c>); handlers depend on the interface so
/// they stay Moqable.</para>
/// </summary>
public interface IExtractionPersister
{
    Task<ExtractionPersistResult> PersistAsync(
        Document document,
        ReadOnlyMemory<byte> pdfBytes,
        IDocTypeExtractor extractor,
        CancellationToken ct);
}

/// <summary>
/// <see cref="WasCacheHit"/> is the signal the endpoint uses to choose 200
/// vs 201. <see cref="Status"/> mirrors the persisted row's terminal state —
/// caller can surface "extraction failed; retry won't help unless the prompt
/// or model changes" to the dashboard.
/// </summary>
public sealed record ExtractionPersistResult(
    int ExtractionId,
    bool WasCacheHit,
    ExtractionStatus Status);
