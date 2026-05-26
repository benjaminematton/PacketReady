using MediatR;
using PacketReady.Application.Payers;
using PacketReady.Application.Providers.Exceptions;
using PacketReady.Application.Scoring.Commands.ComputeReadinessScore;

namespace PacketReady.Api.Endpoints;

/// <summary>
/// Recompute the readiness score for a provider. Always writes a new score row, so
/// repeated POSTs build the historical trail the dashboard renders.
/// <see cref="ProviderNotFoundException"/> and
/// <see cref="PayerNotConfiguredException"/> bubbled by the handler are caught
/// here and shaped into the standard <c>ProblemDetails</c> responses (see
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
            catch (PayerNotConfiguredException ex)
            {
                return ProblemResults.PayerNotConfigured(ex.PayerId, ex.KnownPayerIds);
            }
        })
            .WithName("ComputeReadinessScore")
            .WithTags("Scores")
            .Produces<ReadinessScoreDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return app;
    }
}
