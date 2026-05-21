using MediatR;
using PacketReady.Application.Scoring.Commands.ComputeReadinessScore;

namespace PacketReady.Api.Endpoints;

/// <summary>
/// Recompute the readiness score for a provider. Always writes a new score row, so
/// repeated POSTs build the historical trail the dashboard renders.
/// <see cref="ProviderNotFoundException"/> bubbled by the handler is caught here
/// and shaped into the standard <c>ProblemDetails</c> 404 (see
/// <see cref="ProblemResults"/>) — handlers stay HTTP-agnostic.
/// </summary>
public static class ScoreEndpoint
{
    public static IEndpointRouteBuilder MapScoreEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/providers/{providerId:guid}/scores",
            async (Guid providerId, IMediator mediator, CancellationToken ct) =>
        {
            if (providerId == Guid.Empty)
                return ProblemResults.EmptyProviderId();

            try
            {
                var dto = await mediator.Send(new ComputeReadinessScoreCommand(providerId), ct);
                return Results.Ok(dto);
            }
            catch (ProviderNotFoundException)
            {
                return ProblemResults.ProviderNotFound(providerId);
            }
        })
            .WithName("ComputeReadinessScore")
            .WithTags("Scores")
            .Produces<ReadinessScoreDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }
}
