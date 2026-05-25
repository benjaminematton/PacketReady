using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PacketReady.Application.Abstractions;
using PacketReady.Application.Audit;
using PacketReady.Application.Documents;
using PacketReady.Application.Extraction.Classify;
using PacketReady.Application.Extraction.Extract;
using PacketReady.Application.Extraction.Persist;
using PacketReady.Application.Providers.Exceptions;
using PacketReady.Domain.Audit;
using PacketReady.Domain.Documents;

namespace PacketReady.Application.Extraction.Upload;

/// <summary>
/// Path B — stateful intake. Persists the uploaded PDF to the blob store,
/// classifies it via Haiku, writes a <c>documents</c> row with classifier
/// provenance, then (if doc-type is dispatchable) runs the matching Sonnet
/// extractor and persists a <c>document_extractions</c> row through
/// <see cref="IExtractionPersister"/>.
/// </summary>
public sealed record UploadDocumentCommand(
    Guid ProviderId,
    byte[] PdfBytes,
    string OriginalName,
    string MimeType,
    int PageCount,
    Uploader UploadedBy
) : IRequest<DocumentExtractionResult>;

public sealed class UploadDocumentCommandHandler
    : IRequestHandler<UploadDocumentCommand, DocumentExtractionResult>
{
    // Confidence-band thresholds per spec §"Classifier runtime fallback":
    //   ≥ TrustConfidenceFloor                        → trust the predicted doc_type
    //   OtherFallbackThreshold ≤ x < TrustConfidenceFloor → store predicted; aggregator
    //                                                  emits a Minor "low-confidence
    //                                                  classification" Issue in slice 8
    //   < OtherFallbackThreshold                      → store as Other; no extractor dispatch
    private const double TrustConfidenceFloor = 0.85;
    private const double OtherFallbackThreshold = 0.50;

    private readonly IAppDbContext _db;
    private readonly IBlobStore _blobs;
    private readonly IDocumentClassifier _classifier;
    private readonly IExtractionPersister _persister;
    private readonly IAuditWriter _audit;
    // IKeyedServiceProvider, not IServiceProvider — the handler resolves a keyed
    // extractor and nothing else; the tighter surface prevents drift into
    // service-locator territory.
    private readonly IKeyedServiceProvider _services;
    private readonly TimeProvider _time;
    private readonly ILogger<UploadDocumentCommandHandler> _logger;

    public UploadDocumentCommandHandler(
        IAppDbContext db,
        IBlobStore blobs,
        IDocumentClassifier classifier,
        IExtractionPersister persister,
        IAuditWriter audit,
        IKeyedServiceProvider services,
        TimeProvider time,
        ILogger<UploadDocumentCommandHandler> logger)
    {
        _db = db;
        _blobs = blobs;
        _classifier = classifier;
        _persister = persister;
        _audit = audit;
        _services = services;
        _time = time;
        _logger = logger;
    }

    public async Task<DocumentExtractionResult> Handle(
        UploadDocumentCommand request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProviderId == Guid.Empty)
            throw new ArgumentException("Provider id is required.", nameof(request));
        if (request.PdfBytes is null || request.PdfBytes.Length == 0)
            throw new ArgumentException("PDF bytes are required.", nameof(request));

        // 1. Provider must exist. AsNoTracking — we read to validate, never write
        //    back to the provider row from this handler.
        var providerExists = await _db.Providers
            .AsNoTracking()
            .AnyAsync(p => p.Id == request.ProviderId, ct);
        if (!providerExists)
            throw new ProviderNotFoundException(request.ProviderId);

        // 2. PdfPageCount is precomputed by the endpoint (it already calls
        //    PdfPageCounter to translate parse failures into 400); we trust
        //    the value here. Document.Create enforces >= 1.

        // 3. Upload bytes to blob store first. If classification or DB write
        //    fails, the blob is orphaned — acceptable (P5 may add cleanup).
        //    Alternative considered: classify first to avoid orphan blobs on
        //    classifier failure. Rejected — paying LLM tokens before we know
        //    the durable store is healthy means a blob outage burns budget on
        //    work we can't persist. Cheap orphan beats wasted tokens.
        var pdfMemory = new ReadOnlyMemory<byte>(request.PdfBytes);
        using var pdfStreamForBlob = new MemoryStream(request.PdfBytes, writable: false);
        var storageUri = await _blobs.PutAsync(
            pdfStreamForBlob,
            request.OriginalName,
            request.MimeType,
            ct);

        // 4. Classify before persisting Document — the row carries the
        //    classifier verdict + its provenance.
        var classification = await _classifier.ClassifyAsync(pdfMemory, ct);
        var (persistedDocType, persistedConfidence) = ApplyConfidenceBand(classification);
        LogConfidenceBand(classification, persistedDocType);

        // 5. Persist Document row. Uses the precomputed page count from the
        //    command; Document.Create rejects values < 1 if the endpoint
        //    failed to populate it.
        var document = Document.Create(
            providerId: request.ProviderId,
            docType: persistedDocType,
            docTypeConfidence: persistedConfidence,
            classifierModel: classification.Model,
            classifierPromptHash: classification.PromptHash,
            storageUri: storageUri,
            originalName: request.OriginalName,
            mimeType: request.MimeType,
            pageCount: request.PageCount,
            uploadedBy: request.UploadedBy,
            now: _time.GetUtcNow());

        _db.Documents.Add(document);

        _audit.Stage(AuditEvent.Create(
            eventType: AuditEventType.DocumentUploaded,
            payloadJson: System.Text.Json.JsonSerializer.Serialize(new
            {
                documentId = document.Id,
                docType = persistedDocType.ToWireString(),
                classifierConfidence = classification.Confidence,
                classifierRationale = classification.Rationale,
                storageUri = storageUri,
                pageCount = document.PageCount,
            }),
            providerId: request.ProviderId,
            occurredAt: _time.GetUtcNow()));

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Document persisted: id={DocumentId}, providerId={ProviderId}, docType={DocType}, conf={Conf:F2}",
            document.Id, request.ProviderId, persistedDocType, classification.Confidence);

        // 6. If Other or Cv, no extractor is registered — return without an
        //    extractionId. Aggregator skips both doc types entirely (slice 8).
        if (persistedDocType is DocType.Other or DocType.Cv)
        {
            return new DocumentExtractionResult(
                DocumentId: document.Id,
                DocType: persistedDocType,
                DocTypeConfidence: persistedConfidence,
                ExtractionId: null,
                WasCacheHit: false);
        }

        // 7. Dispatch the keyed extractor and persist the extraction row.
        //    GetRequiredKeyedService throws InvalidOperationException for an
        //    unregistered key — that's an internal wiring bug, surfaces as a
        //    500. The endpoint pre-checks dispatchability.
        var extractor = _services.GetRequiredKeyedService<IDocTypeExtractor>(persistedDocType);
        var persistResult = await _persister.PersistAsync(document, pdfMemory, extractor, ct);

        return new DocumentExtractionResult(
            DocumentId: document.Id,
            DocType: persistedDocType,
            DocTypeConfidence: persistedConfidence,
            ExtractionId: persistResult.ExtractionId,
            WasCacheHit: persistResult.WasCacheHit);
    }

    // Per-band mapping. Confidence is always reported as the classifier said
    // (so the dashboard can show "what the model thought"); doc_type is what
    // we PERSIST, which differs from the prediction only in the Other band.
    private static (DocType PersistedDocType, double PersistedConfidence) ApplyConfidenceBand(
        ClassificationResult c)
    {
        if (c.Confidence < OtherFallbackThreshold)
            return (DocType.Other, c.Confidence);

        // Mid + trust bands both persist the predicted type. The mid-band Minor
        // Issue is emitted by the aggregator (slice 8), not here.
        return (c.DocType, c.Confidence);
    }

    // Real-time operator signal for classifier drift: log which band the
    // confidence landed in. Keeps the band thresholds load-bearing in this
    // file (otherwise TrustConfidenceFloor would be documentation-only).
    private void LogConfidenceBand(ClassificationResult c, DocType persistedDocType)
    {
        if (c.Confidence < OtherFallbackThreshold)
        {
            _logger.LogInformation(
                "Classifier confidence {Conf:F2} below {Floor:F2} — persisted as Other (predicted was {Predicted}).",
                c.Confidence, OtherFallbackThreshold, c.DocType);
        }
        else if (c.Confidence < TrustConfidenceFloor)
        {
            _logger.LogInformation(
                "Classifier confidence {Conf:F2} in mid band [{Lo:F2}, {Hi:F2}); persisted predicted {DocType}, aggregator will flag Minor.",
                c.Confidence, OtherFallbackThreshold, TrustConfidenceFloor, persistedDocType);
        }
    }
}
