using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PacketReady.Application.Abstractions;
using PacketReady.Application.Documents;
using PacketReady.Application.Extraction.Extract;
using PacketReady.Application.Extraction.Persist;
using PacketReady.Domain.Documents;

namespace PacketReady.Application.Extraction.Reextract;

/// <summary>
/// Re-runs the extractor against an existing <see cref="Document"/>. Same
/// idempotency key as the upload path — if the model and prompt hash haven't
/// changed, returns the cached extraction (cache hit). Otherwise: rents a
/// fresh extraction_id via the advisory-locked path and persists.
///
/// <para>Useful in two scenarios: a transient extractor failure (retry the
/// Failed row by force; spec says caller would have to bump model/prompt to
/// get a different result — for P3 we surface that as a cache hit), and a
/// model/prompt bump (P4+ when Sonnet 4.6 → 4.7 invalidates the cache).</para>
/// </summary>
public sealed record ReextractDocumentCommand(Guid DocumentId)
    : IRequest<DocumentExtractionResult>;

public sealed class ReextractDocumentCommandHandler
    : IRequestHandler<ReextractDocumentCommand, DocumentExtractionResult>
{
    private readonly IAppDbContext _db;
    private readonly IBlobStore _blobs;
    private readonly IExtractionPersister _persister;
    // IKeyedServiceProvider, not IServiceProvider — see UploadDocumentCommandHandler.
    private readonly IKeyedServiceProvider _services;
    private readonly ILogger<ReextractDocumentCommandHandler> _logger;

    public ReextractDocumentCommandHandler(
        IAppDbContext db,
        IBlobStore blobs,
        IExtractionPersister persister,
        IKeyedServiceProvider services,
        ILogger<ReextractDocumentCommandHandler> logger)
    {
        _db = db;
        _blobs = blobs;
        _persister = persister;
        _services = services;
        _logger = logger;
    }

    public async Task<DocumentExtractionResult> Handle(
        ReextractDocumentCommand request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DocumentId == Guid.Empty)
            throw new ArgumentException("Document id is required.", nameof(request));

        var document = await _db.Documents
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == request.DocumentId, ct);
        if (document is null)
            throw new DocumentNotFoundException(request.DocumentId);

        // Reextract on Other or Cv has no extractor to dispatch — the doc was
        // either too low-confidence to trust at upload time or a CV (P4
        // territory). Surface as a no-op rather than re-classifying.
        if (document.DocType is null or DocType.Other or DocType.Cv)
        {
            _logger.LogInformation(
                "Reextract no-op: documentId={DocumentId}, docType={DocType} has no registered extractor",
                document.Id, document.DocType);
            return new DocumentExtractionResult(
                DocumentId: document.Id,
                DocType: document.DocType ?? DocType.Other,
                DocTypeConfidence: document.DocTypeConfidence ?? 0,
                ExtractionId: null,
                WasCacheHit: false);
        }

        // Load PDF bytes from blob storage. The stream returned by GetAsync is
        // owned by us; dispose to release the file handle.
        await using var pdfStream = await _blobs.GetAsync(document.StorageUri, ct);
        await using var ms = new MemoryStream();
        await pdfStream.CopyToAsync(ms, ct);
        var pdfBytes = ms.ToArray();

        var extractor = _services.GetRequiredKeyedService<IDocTypeExtractor>(document.DocType.Value);

        var persistResult = await _persister.PersistAsync(
            document, pdfBytes, extractor, ct);

        return new DocumentExtractionResult(
            DocumentId: document.Id,
            DocType: document.DocType.Value,
            DocTypeConfidence: document.DocTypeConfidence ?? 0,
            ExtractionId: persistResult.ExtractionId,
            WasCacheHit: persistResult.WasCacheHit);
    }
}

/// <summary>
/// Thrown when <see cref="ReextractDocumentCommand.DocumentId"/> doesn't match
/// any documents row. API layer catches and maps to 404 — handlers stay
/// HTTP-agnostic.
/// </summary>
public sealed class DocumentNotFoundException(Guid documentId)
    : Exception($"Document {documentId} not found.")
{
    public Guid DocumentId { get; } = documentId;
}
