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
    private const string EmptyDocumentIdType          = "urn:packetready:error:empty_document_id";
    private const string ExtractMissingFileType       = "urn:packetready:error:extract_missing_file";
    private const string ExtractInvalidDocTypeType    = "urn:packetready:error:extract_invalid_doc_type";
    private const string ExtractDocTypeUnimplementedType  = "urn:packetready:error:extract_doc_type_unimplemented";
    private const string DocumentNotFoundType         = "urn:packetready:error:document_not_found";
    private const string DocumentBlobMissingType      = "urn:packetready:error:document_blob_missing";
    private const string UploadInvalidPdfType         = "urn:packetready:error:upload_invalid_pdf";
    private const string IntakeAlreadyExistsType      = "urn:packetready:error:intake_already_exists";
    private const string MagicLinkInvalidType         = "urn:packetready:error:magic_link_invalid";

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

    public static IResult EmptyDocumentId() =>
        Results.Problem(
            type: EmptyDocumentIdType,
            title: "documentId must be a non-empty Guid.",
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

    /// <summary>
    /// Document row exists but its storage URI doesn't resolve to a readable
    /// blob — documents and blob store drifted out of sync (blob store wiped,
    /// restored from older backup, scheme rolled back post-cutover, etc.).
    /// 410 Gone signals "this resource existed but is no longer available",
    /// distinct from 404 "never existed". <c>storageUri</c> is intentionally
    /// not surfaced in the body — it would leak deployment filesystem layout
    /// to anyone who can hit the endpoint.
    /// </summary>
    public static IResult DocumentBlobMissing(Guid documentId) =>
        Results.Problem(
            type: DocumentBlobMissingType,
            title: "Document blob is missing from storage.",
            detail: $"Document {documentId} is recorded but its underlying file is no longer reachable.",
            statusCode: StatusCodes.Status410Gone,
            extensions: new Dictionary<string, object?>
            {
                ["documentId"] = documentId,
            });

    /// <summary>
    /// An <c>intake_sessions</c> row already exists for the target provider.
    /// 409 (not 400) because the request shape is valid; the conflict is
    /// with persisted state. Re-issuing a fresh magic link for an existing
    /// intake is a separate endpoint (deferred); this signals "use that
    /// instead, don't double-start."
    /// </summary>
    public static IResult IntakeAlreadyExists(Guid providerId) =>
        Results.Problem(
            type: IntakeAlreadyExistsType,
            title: "An intake session already exists for this provider.",
            detail: $"Provider {providerId} already has an active intake. Re-issue the magic link instead of starting a new intake.",
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?> { ["providerId"] = providerId });

    /// <summary>
    /// Magic-link token failed validation. 410 Gone signals "this URL was
    /// valid but isn't anymore" — applies to expired, consumed, and
    /// not-found cases. <c>reason</c> is the
    /// <c>MagicLinkInvalidReason</c> enum member name verbatim so the
    /// portal page can branch ("link expired" vs "link already used" vs
    /// "this link doesn't exist") without parsing the title string.
    ///
    /// <para>Malformed / bad-signature lands here too as 410, not 400 —
    /// the surface area for "your URL is tampered" is identical to
    /// "your URL is too old" from the provider's point of view, and
    /// distinguishing them in the response is mostly useful to
    /// attackers.</para>
    /// </summary>
    public static IResult MagicLinkInvalid(string reason) =>
        Results.Problem(
            type: MagicLinkInvalidType,
            title: "Magic link is no longer valid.",
            detail: "This link can't be used. Ask the admin to issue a fresh one.",
            statusCode: StatusCodes.Status410Gone,
            extensions: new Dictionary<string, object?> { ["reason"] = reason });
}
