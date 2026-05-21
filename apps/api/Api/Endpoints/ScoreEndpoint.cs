using MediatR;
using PacketReady.Application.Scoring.Commands.ComputeReadinessScore;

namespace PacketReady.Api.Endpoints;

/// <summary>
/// Recompute the readiness score for a provider. Always writes a new score row, so
/// repeated POSTs build the historical trail the dashboard renders. <see cref="Results.NotFound()"/>
/// when the provider doesn't exist; the handler signals this via
/// <see cref="ProviderNotFoundException"/> and the endpoint keeps that exception
/// from leaking through as a 500.
/// </summary>
public static class ScoreEndpoint
{
    public static IEndpointRouteBuilder MapScoreEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/providers/{providerId:guid}/scores",
            async (Guid providerId, IMediator mediator, CancellationToken ct) =>
        {
            if (providerId == Guid.Empty)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["providerId"] = ["providerId must be a non-empty Guid."],
                });

            try
            {
                var dto = await mediator.Send(new ComputeReadinessScoreCommand(providerId), ct);
                return Results.Ok(dto);
            }
            catch (ProviderNotFoundException)
            {
                return Results.NotFound(new { providerId, error = "provider_not_found" });
            }
        });

        return app;
    }
}
