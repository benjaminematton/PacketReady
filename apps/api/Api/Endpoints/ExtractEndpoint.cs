using Microsoft.AspNetCore.Mvc;

namespace PacketReady.Api.Endpoints;

/// <summary>
/// P2 stub. Returns an empty <c>fields</c> dictionary so the Python eval
/// runner has a contract-shaped endpoint to call against. P3 replaces the
/// body with the Haiku-classify → Sonnet-extract flow; the request and
/// response SHAPE here are the locked contract:
///
/// <code>
/// POST /api/extract  multipart/form-data
///   file:    PDF bytes (form field "file")
///   docType: license | dea | boardCert | malpractice | cv
///
/// 200 OK  application/json   { "fields": { ... } }
/// 4xx     application/problem+json   RFC 7807 ProblemDetails
/// </code>
///
/// <para>P2 ignores both inputs deliberately — exercising the harness, not
/// the model. P3 will start binding the file payload + docType and the
/// signature here doesn't change.</para>
/// </summary>
public static class ExtractEndpoint
{
    // Multipart upload cap. Scanned packets (P5) can run ~20 MB per PDF and
    // we expect single-doc requests; 32 MB leaves headroom without inviting
    // accidental DOS via 1 GB payloads. Revisit if the intake portal starts
    // bundling multi-doc requests.
    private const long MaxUploadBytes = 32L * 1024 * 1024;

    private static readonly string[] AllowedDocTypes =
    {
        "license", "dea", "boardCert", "malpractice", "cv",
    };

    public static IEndpointRouteBuilder MapExtractEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/extract", (
                IFormFile file,
                [FromForm] string docType) =>
            {
                if (file is null || file.Length == 0)
                    return ProblemResults.ExtractMissingFile();

                if (string.IsNullOrWhiteSpace(docType) || !AllowedDocTypes.Contains(docType))
                    return ProblemResults.ExtractInvalidDocType(AllowedDocTypes);

                return Results.Ok(new { fields = new Dictionary<string, object?>() });
            })
            .WithName("ExtractDocument")
            .WithTags("Extraction")
            // Multipart form POST from a CLI runner — no cookies, no XSRF
            // surface to protect. P5 intake portal will need its own anti-
            // forgery story; revisit then.
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes))
            .Accepts<IFormFile>("multipart/form-data")
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

        return app;
    }
}
