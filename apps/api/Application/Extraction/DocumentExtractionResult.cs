using PacketReady.Domain.Documents;

namespace PacketReady.Application.Extraction;

/// <summary>
/// Shared return shape for the upload and reextract handlers: a persisted
/// <see cref="Document"/> plus (optionally) a persisted <c>DocumentExtraction</c>.
///
/// <para><see cref="ExtractionId"/> is null when no extractor was dispatched
/// (classifier returned <c>Other</c>, or doc type is <c>Cv</c>).
/// <see cref="WasCacheHit"/> is always false on upload (each call mints a fresh
/// <see cref="DocumentId"/>); meaningful only on reextract, where it tells the
/// caller "I returned the cached extraction; no LLM tokens were charged."</para>
/// </summary>
public sealed record DocumentExtractionResult(
    Guid DocumentId,
    DocType DocType,
    double DocTypeConfidence,
    int? ExtractionId,
    bool WasCacheHit);
