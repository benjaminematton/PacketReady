using MediatR;
using PacketReady.Application.Intake.Commands.StartIntake;
using PacketReady.Application.Intake.Exceptions;
using PacketReady.Application.Providers.Exceptions;

namespace PacketReady.Api.Endpoints.Intake;

/// <summary>
/// Admin endpoint: start the intake lifecycle for an existing provider.
/// Mirrors <c>ScoreEndpoint</c>'s try/catch shape — handler stays
/// HTTP-agnostic, this layer maps domain exceptions to
/// <see cref="ProblemResults"/>.
///
/// <para>The signed magic-link token rides in the response body.
/// The admin is responsible for getting it to the provider — for C3 by
/// copy-pasting; the email dispatcher (C5) takes over once
/// <c>OutboundMessage</c> composition is wired into the command.</para>
/// </summary>
public static class StartIntakeEndpoint
{
    public sealed record StartIntakeRequest(Guid ProviderId);

    public static IEndpointRouteBuilder MapStartIntakeEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/intakes",
            async (StartIntakeRequest body, IMediator mediator, CancellationToken ct) =>
        {
            if (body is null || body.ProviderId == Guid.Empty)
                return ProblemResults.EmptyProviderId();

            try
            {
                var result = await mediator.Send(
                    new StartIntakeCommand(body.ProviderId), ct);
                return Results.Created(
                    $"/api/intakes/{result.IntakeSessionId}",
                    result);
            }
            catch (ProviderNotFoundException)
            {
                return ProblemResults.ProviderNotFound(body.ProviderId);
            }
            catch (IntakeAlreadyExistsException)
            {
                return ProblemResults.IntakeAlreadyExists(body.ProviderId);
            }
        })
            .WithName("StartIntake")
            .WithTags("Intake")
            .Produces<StartIntakeResult>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }
}
