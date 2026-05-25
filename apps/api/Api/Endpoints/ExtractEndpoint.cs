using System.Text.Json.Nodes;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using PacketReady.Application.Extraction.Extract;
using PacketReady.Domain.Documents;

namespace PacketReady.Api.Endpoints;

/// <summary>
/// Path A — stateless field extraction. Receives a single PDF + docType,
/// dispatches to the keyed Sonnet extractor, returns a flat
/// <c>{ "fields": { ... } }</c> body for the Python eval runner. No DB writes,
/// no classifier call, no idempotency cache (the runner already knows the
/// docType from <c>golden.json</c>).
///
/// <para>The request shape (<c>file</c> form field + <c>docType</c>) and
/// response shape (<c>{ "fields": ... }</c>) are P2-locked; the body changed
/// in P3 from a stubbed empty dict to a real extractor dispatch, but the
/// surface did not.</para>
/// </summary>
public static class ExtractEndpoint
{
    // Multipart upload cap. Scanned packets (P5) can run ~20 MB per PDF and
    // we expect single-doc requests; 32 MB leaves headroom without inviting
    // accidental DOS via 1 GB payloads. Revisit if the intake portal starts
    // bundling multi-doc requests.
    //
    // **Enforcement note:** on minimal API endpoints the
    // RequestSizeLimitAttribute metadata below is NOT honored by Kestrel — it
    // applies to MVC. The actual ceiling is Kestrel's server-wide
    // MaxRequestBodySize, which Program.cs lifts to this same value. The
    // attribute is kept for documentation + Swagger.
    internal const long MaxUploadBytes = 32L * 1024 * 1024;

    // Wire-format → DocType enum. camelCase strings on the wire, PascalCase
    // enum internally. The string set IS the locked P2 surface; the runner
    // sends lowercase docType values, and dropping any of these would break
    // the eval-runner contract.
    private static readonly Dictionary<string, DocType> DocTypeWireMap = new(StringComparer.Ordinal)
    {
        ["license"]     = DocType.License,
        ["dea"]         = DocType.Dea,
        ["boardCert"]   = DocType.BoardCert,
        ["malpractice"] = DocType.Malpractice,
        ["cv"]          = DocType.Cv,
    };

    public static IEndpointRouteBuilder MapExtractEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/extract", async (
                IFormFile file,
                [FromForm] string docType,
                IMediator mediator,
                IServiceProvider services,
                CancellationToken ct) =>
            {
                if (file is null || file.Length == 0)
                    return ProblemResults.ExtractMissingFile();

                if (string.IsNullOrWhiteSpace(docType) ||
                    !DocTypeWireMap.TryGetValue(docType, out var resolved))
                    return ProblemResults.ExtractInvalidDocType(DocTypeWireMap.Keys);

                // DI is the source of truth for "do we ship an extractor for
                // this doc-type yet?". Slice 4 ships License; slice 5 promotes
                // Dea, BoardCert, Malpractice; CV stays unwired through P3 and
                // surfaces here as a 400, not a 500 from GetRequiredKeyedService.
                if (services.GetKeyedService<IDocTypeExtractor>(resolved) is null)
                    return ProblemResults.ExtractDocTypeNotYetSupported(docType);

                // Materialize the upload to a byte[] for the command. The cap above
                // bounds this; 32 MB on a long-running API process is the accepted
                // working-set cost for the simpler stateless design (no streaming
                // hand-off to Sonnet — the Anthropic SDK takes bytes anyway).
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms, ct);

                var result = await mediator.Send(
                    new ExtractInMemoryCommand(ms.ToArray(), resolved),
                    ct);

                // Wire-shape: { "fields": <the value-only JSONB the extractor produced> }.
                // ExtractionResult.FieldsJson is ALREADY value-only — Sonnet's per-field
                // envelope was split in the extractor base. field_locations and
                // confidence are discarded on Path A (no DB write, no runner consumer).
                //
                // Anonymous-type lowercase `fields` matches the P2-locked wire shape
                // regardless of any future JsonNamingPolicy change on HttpJsonOptions.
                var fields = JsonNode.Parse(result.FieldsJson)
                    ?? throw new InvalidOperationException(
                        "Extractor returned non-JSON fields payload — contract violation.");

                return Results.Ok(new { fields });
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
