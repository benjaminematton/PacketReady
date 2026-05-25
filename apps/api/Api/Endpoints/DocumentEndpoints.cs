using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using PacketReady.Application.Abstractions;
using PacketReady.Application.Documents;
using PacketReady.Application.Extraction;
using PacketReady.Application.Extraction.Reextract;
using PacketReady.Application.Extraction.Upload;
using PacketReady.Application.Providers.Exceptions;
using PacketReady.Domain.Documents;
using PacketReady.Infrastructure.Extraction;

namespace PacketReady.Api.Endpoints;

/// <summary>
/// Path B — stateful intake. Two routes:
///
/// <list type="bullet">
///   <item><c>POST /api/providers/{id}/documents</c> — multipart PDF upload;
///   blob put → classify → persist documents row → (if dispatchable) extract +
///   persist extraction. Returns 201 with <c>{ documentId, docType,
///   docTypeConfidence, extractionId }</c>.</item>
///   <item><c>POST /api/documents/{id}/reextract</c> — re-runs the extractor
///   against an existing document. Returns 200 with the same body shape.
///   Idempotent on <c>(documentId, schemaVersion, model, promptHash)</c> — a
///   re-extract against unchanged inputs returns the cached extractionId
///   without re-billing the LLM.</item>
/// </list>
///
/// <para>Status-code discipline: <b>201</b> on first-time persistence, <b>200</b>
/// on idempotency cache hit (so the caller can tell "I caused this" from
/// "this already existed" without parsing the body). Per the slice-1 plan,
/// upload always mints a fresh documentId so it's always 201; reextract
/// uses the hit/miss split.</para>
/// </summary>
public static class DocumentEndpoints
{
    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        // Path B intake — upload one PDF, classify, extract.
        app.MapPost("/api/providers/{providerId:guid}/documents", async (
                Guid providerId,
                IFormFile file,
                PdfPageCounter pageCounter,
                IMediator mediator,
                CancellationToken ct) =>
            {
                if (providerId == Guid.Empty)
                    return ProblemResults.EmptyProviderId();
                if (file is null || file.Length == 0)
                    return ProblemResults.ExtractMissingFile();

                // Read the upload to bytes once. Cap is enforced by Kestrel's
                // server-wide MaxRequestBodySize (lifted in Program.cs to the
                // shared MaxUploadBytes value); reaching here means under-cap.
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms, ct);
                var pdfBytes = ms.ToArray();

                // PDF parse validation lives at the endpoint boundary so a
                // garbled-bytes upload surfaces as 400 Problem, not 500 from
                // a deeper layer. The handler trusts request.PageCount; the
                // parse + count happen here.
                int pageCount;
                try
                {
                    pageCount = pageCounter.Read(pdfBytes).PageCount;
                }
                catch (InvalidPdfException ex)
                {
                    return ProblemResults.UploadInvalidPdf(ex.Message);
                }

                try
                {
                    var result = await mediator.Send(new UploadDocumentCommand(
                        ProviderId: providerId,
                        PdfBytes: pdfBytes,
                        OriginalName: file.FileName,
                        MimeType: string.IsNullOrWhiteSpace(file.ContentType)
                            ? "application/pdf"
                            : file.ContentType,
                        PageCount: pageCount,
                        // P3 only has provider-initiated uploads via this route;
                        // a P5 admin portal will need its own surface to set Admin.
                        UploadedBy: Uploader.Provider), ct);

                    return Results.Created(
                        uri: $"/api/documents/{result.DocumentId}",
                        value: ToResponse(result));
                }
                catch (ProviderNotFoundException)
                {
                    return ProblemResults.ProviderNotFound(providerId);
                }
            })
            .WithName("UploadDocument")
            .WithTags("Documents")
            // Antiforgery is off because this is a programmatic multipart upload
            // — no browser-form/cookie auth path, so there's no CSRF surface to
            // protect. Auth happens at the gateway (P5); revisit if a browser
            // upload flow is ever added.
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(ExtractEndpoint.MaxUploadBytes))
            .Accepts<IFormFile>("multipart/form-data")
            .Produces(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

        // Path B reextract — pull bytes from blob store, re-run extractor.
        app.MapPost("/api/documents/{documentId:guid}/reextract", async (
                Guid documentId,
                IMediator mediator,
                CancellationToken ct) =>
            {
                if (documentId == Guid.Empty)
                    return ProblemResults.EmptyDocumentId();

                try
                {
                    var result = await mediator.Send(
                        new ReextractDocumentCommand(documentId), ct);

                    // Always 200 — the Document already existed (404 otherwise).
                    // The idempotency hit/miss is exposed via wasCacheHit in the
                    // body for clients that care; status code stays consistent.
                    return Results.Ok(ToResponse(result));
                }
                catch (DocumentNotFoundException)
                {
                    return ProblemResults.DocumentNotFound(documentId);
                }
            })
            .WithName("ReextractDocument")
            .WithTags("Documents")
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Citation drill-in — dashboard fetches the source PDF for an Issue's
        // Citation.DocumentId. Streams the bytes from blob storage; the
        // documents row's MIME type goes on the Content-Type header. No
        // auth in P3 — same posture as the rest of the API; P5's intake
        // portal will need its own session story.
        app.MapGet("/api/documents/{documentId:guid}/blob", async (
                Guid documentId,
                HttpContext http,
                IAppDbContext db,
                IBlobStore blobs,
                CancellationToken ct) =>
            {
                if (documentId == Guid.Empty)
                    return ProblemResults.EmptyDocumentId();

                var doc = await db.Documents
                    .AsNoTracking()
                    .Where(d => d.Id == documentId)
                    .Select(d => new { d.StorageUri, d.MimeType, d.OriginalName })
                    .FirstOrDefaultAsync(ct);
                if (doc is null)
                    return ProblemResults.DocumentNotFound(documentId);

                Stream stream;
                try
                {
                    stream = await blobs.GetAsync(doc.StorageUri, ct);
                }
                catch (FileNotFoundException)
                {
                    // Document row points at a blob that doesn't exist on disk
                    // — the documents row and the blob got out of sync (blob
                    // store wiped, restored from backup, etc.). 410 Gone is
                    // honest: the resource WAS here, isn't now.
                    return ProblemResults.DocumentBlobMissing(documentId);
                }
                catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException)
                {
                    // documents.storage_uri is malformed or resolves outside the
                    // configured blob root — same drift class as FileNotFound
                    // (tampered row, post-cutover scheme mismatch, root path
                    // moved). Same 410 lane: the resource is unreachable.
                    return ProblemResults.DocumentBlobMissing(documentId);
                }

                // Inline Content-Disposition with a filename hint so the
                // dashboard's PDF viewer renders the response in-place, and
                // direct navigation to /blob still gets a sensible filename
                // if a user does Save As. Results.File's fileDownloadName
                // overload forces attachment, so set the header ourselves.
                http.Response.Headers.ContentDisposition =
                    new ContentDispositionHeaderValue("inline")
                    {
                        FileNameStar = doc.OriginalName,
                    }.ToString();

                return Results.File(
                    fileStream: stream,
                    contentType: doc.MimeType,
                    enableRangeProcessing: true);
            })
            .WithName("DownloadDocumentBlob")
            .WithTags("Documents")
            .Produces(StatusCodes.Status200OK, contentType: "application/pdf")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status410Gone);

        return app;
    }

    // camelCase wire payload — anonymous type so System.Text.Json honors the
    // property names verbatim regardless of any future PropertyNamingPolicy.
    private static object ToResponse(DocumentExtractionResult r) => new
    {
        documentId = r.DocumentId,
        docType = r.DocType.ToWireString(),
        docTypeConfidence = r.DocTypeConfidence,
        extractionId = r.ExtractionId,
        wasCacheHit = r.WasCacheHit,
    };
}
