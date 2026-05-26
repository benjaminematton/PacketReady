using MediatR;
using PacketReady.Application.Payers;
using PacketReady.Application.Providers.Commands.CreateProvider;
using PacketReady.Application.Providers.Queries.GetProviderDetail;
using PacketReady.Application.Providers.Queries.ListProviders;
using PacketReady.Application.Scoring.Commands.ComputeReadinessScore;

namespace PacketReady.Api.Endpoints;

/// <summary>
/// Read endpoints for the dashboard plus the P4 create endpoint that the
/// eval orchestrator uses to seed providers per packet.
///
/// <para>Error contract: 4xx responses use RFC7807 <c>ProblemDetails</c> with a
/// machine-readable <c>type</c> URN (<c>urn:packetready:error:provider_not_found</c>)
/// the dashboard can branch on. Keeps every error response on this API the same
/// shape — no bespoke <c>{ error: "..." }</c> objects.</para>
/// </summary>
public static class ProviderEndpoints
{
    public static IEndpointRouteBuilder MapProviderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/providers", async (IMediator mediator, CancellationToken ct) =>
        {
            var rows = await mediator.Send(new ListProvidersQuery(), ct);
            return Results.Ok(rows);
        })
            .WithName("ListProviders")
            .WithTags("Providers")
            .Produces<IReadOnlyList<ProviderListItemDto>>(StatusCodes.Status200OK);

        app.MapGet("/api/providers/{providerId:guid}",
            async (Guid providerId, IMediator mediator, CancellationToken ct) =>
        {
            if (providerId == Guid.Empty)
                return ProblemResults.EmptyProviderId();

            var dto = await mediator.Send(new GetProviderDetailQuery(providerId), ct);
            return dto is null ? ProblemResults.ProviderNotFound(providerId) : Results.Ok(dto);
        })
            .WithName("GetProviderDetail")
            .WithTags("Providers")
            .Produces<ProviderDetailDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // POST /api/providers — minimal create surface, used by the P4 eval
        // orchestrator and (P5+) the admin intake portal. Both `payerId` and
        // `identity` are optional and default to placeholders
        // (Provider.DefaultPayerId / ProviderIdentityValidator.Placeholder).
        // The body itself is nullable so a no-body POST hits the same
        // all-placeholder path — that's the P5 admin "create empty, fill via
        // portal" route. (.NET 8 minimal-API rule: a nullable reference param
        // permits an empty request body.) Wire-shape violations (identity
        // fields and / or payerId blank-when-supplied) collect into a single
        // 400 with the full violations list; payer resolution runs in the
        // handler and surfaces as a 422 via PayerNotConfiguredException.
        app.MapPost("/api/providers",
            async (CreateProviderRequest? body, IMediator mediator,
                   TimeProvider clock, CancellationToken ct) =>
        {
            var violations = new List<string>(capacity: 4);

            // Whitespace PayerId is operator error — Provider.Create rejects
            // it as ArgumentException; we reject it here so the caller sees
            // a 400 with a useful message instead of a 500.
            if (body?.PayerId is { } payerId && string.IsNullOrWhiteSpace(payerId))
                violations.Add("payerId must be omitted (to use the default) or a non-blank string.");

            var identity = body?.Identity;
            if (identity is not null)
            {
                var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
                violations.AddRange(ProviderIdentityValidator.Validate(identity, today));
            }

            if (violations.Count > 0)
                return ProblemResults.InvalidProviderIdentity(violations);

            try
            {
                var id = await mediator.Send(
                    new CreateProviderCommand(body?.PayerId, identity), ct);
                return Results.Created(
                    uri: $"/api/providers/{id}",
                    value: new CreateProviderResponse(id));
            }
            catch (PayerNotConfiguredException ex)
            {
                return ProblemResults.PayerNotConfigured(ex.PayerId, ex.KnownPayerIds);
            }
        })
            .WithName("CreateProvider")
            .WithTags("Providers")
            .Produces<CreateProviderResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return app;
    }
}

/// <summary>
/// Wire-format DTO for the create endpoint. Both fields are optional —
/// see the endpoint docstring for default behavior. Distinct from
/// <see cref="CreateProviderCommand"/> so a future wire change (e.g.
/// adding <c>adminInitiatedBy</c> in P5) doesn't drag the mediator
/// contract along.
/// </summary>
public sealed record CreateProviderRequest(string? PayerId, ProviderIdentityDto? Identity);

/// <summary>
/// Response shape for the create endpoint. <see cref="Id"/>-only by design;
/// callers fetch the full projection via <c>GET /api/providers/{id}</c>.
/// </summary>
public sealed record CreateProviderResponse(Guid Id);
