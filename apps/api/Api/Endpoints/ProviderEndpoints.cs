using MediatR;
using PacketReady.Application.Providers.Queries.GetProviderDetail;
using PacketReady.Application.Providers.Queries.ListProviders;
using PacketReady.Application.Scoring.Commands.ComputeReadinessScore;

namespace PacketReady.Api.Endpoints;

/// <summary>
/// Read endpoints for the dashboard. Writes live on <see cref="ScoreEndpoint"/>
/// (scores) and a future P5 endpoint for provider creation. P1 has no create-provider
/// path — fixtures are seeded by a CLI.
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

        return app;
    }
}
