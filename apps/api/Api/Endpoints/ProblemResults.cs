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
    private const string ProviderNotFoundType       = "urn:packetready:error:provider_not_found";
    private const string EmptyProviderIdType        = "urn:packetready:error:empty_provider_id";
    private const string ExtractMissingFileType     = "urn:packetready:error:extract_missing_file";
    private const string ExtractInvalidDocTypeType  = "urn:packetready:error:extract_invalid_doc_type";

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
}
