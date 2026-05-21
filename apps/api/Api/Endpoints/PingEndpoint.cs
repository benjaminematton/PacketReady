using MediatR;
using PacketReady.Application.Ping;

namespace PacketReady.Api.Endpoints;

public static class PingEndpoint
{
    public static IEndpointRouteBuilder MapPingEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/ping", async (PingRequest req, IMediator mediator, CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Message))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(PingRequest.Message)] = ["Message is required."],
                });

            var result = await mediator.Send(new PingCommand(req.Message), ct);
            return Results.Ok(result);
        });

        return app;
    }

    public sealed record PingRequest(string Message);
}
