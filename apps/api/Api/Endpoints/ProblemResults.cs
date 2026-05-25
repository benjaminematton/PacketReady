namespace PacketReady.Api.Endpoints;

/// <summary>
/// Single source of truth for 4xx <c>ProblemDetails</c> responses. The dashboard
/// branches on the <c>type</c> URN (a stable machine identifier), not on
/// <c>title</c> or status code, so a future copy tweak doesn't break clients.
///
/// <para>Keeping this in one place prevents the small drift that turns into
/// "is the field called <c>error</c> or <c>code</c>?" tickets a quarter from now.</para>
/// </summary>
internal static class ProblemResults
{
    private const string ProviderNotFoundType         = "urn:packetready:error:provider_not_found";
    private const string EmptyProviderIdType          = "urn:packetready:error:empty_provider_id";
    private const string ExtractMissingFileType       = "urn:packetready:error:extract_missing_file";
    private const string ExtractInvalidDocTypeType    = "urn:packetready:error:extract_invalid_doc_type";
    private const string ExtractDocTypeUnimplementedType  = "urn:packetready:error:extract_doc_type_unimplemented";
    private const string DocumentNotFoundType         = "urn:packetready:error:document_not_found";
    private const string UploadInvalidPdfType         = "urn:packetready:error:upload_invalid_pdf";

    public static IResult ProviderNotFound(Guid providerId) =>
        Results.Problem(
            type: ProviderNotFoundType,
            title: "Provider not found.",
            detail: $"No provider exists with id {providerId}.",
            statusCode: StatusCodes.Status404NotFound,
            extensions: new Dictionary<string, object?> { ["providerId"] = providerId });

    public static IResult EmptyProviderId() =>
        Results.Problem(
            type: EmptyProviderIdType,
            title: "providerId must be a non-empty Guid.",
            statusCode: StatusCodes.Status400BadRequest);

    public static IResult ExtractMissingFile() =>
        Results.Problem(
            type: ExtractMissingFileType,
            title: "file is required (multipart form field).",
            statusCode: StatusCodes.Status400BadRequest);

    public static IResult ExtractInvalidDocType(IEnumerable<string> allowed) =>
        Results.Problem(
            type: ExtractInvalidDocTypeType,
            title: $"docType must be one of: {string.Join(", ", allowed)}.",
            statusCode: StatusCodes.Status400BadRequest);

    /// <summary>
    /// Doc-type is in the wire-shape allowlist (recognized by clients) but no
    /// extractor for it has been wired yet. CV is the only current case — slated
    /// for P4. Returns 400 (not 501) so the eval runner's regression gate treats
    /// it as a client-side "skip", not a server outage.
    /// </summary>
    public static IResult ExtractDocTypeNotYetSupported(string docType) =>
        Results.Problem(
            type: ExtractDocTypeUnimplementedType,
            title: $"docType '{docType}' is not yet supported by this build.",
            statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?> { ["docType"] = docType });

    public static IResult DocumentNotFound(Guid documentId) =>
        Results.Problem(
            type: DocumentNotFoundType,
            title: "Document not found.",
            detail: $"No document exists with id {documentId}.",
            statusCode: StatusCodes.Status404NotFound,
            extensions: new Dictionary<string, object?> { ["documentId"] = documentId });

    /// <summary>
    /// PdfPig couldn't parse the uploaded bytes. 400 with the parser's
    /// message so the client knows what shape was rejected. No documents row
    /// is written; the blob is never uploaded.
    /// </summary>
    public static IResult UploadInvalidPdf(string parserMessage) =>
        Results.Problem(
            type: UploadInvalidPdfType,
            title: "Uploaded file could not be parsed as a PDF.",
            detail: parserMessage,
            statusCode: StatusCodes.Status400BadRequest);
}
