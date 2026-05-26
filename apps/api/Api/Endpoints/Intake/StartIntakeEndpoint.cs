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
/// <para>The signed magic-link token rides in the response body so a
/// curl-based admin can still grab it directly; C5's
/// <c>OutboxDispatcherJob</c> also sends it via
/// <c>MockSmtpSender</c> to <c>email</c>.</para>
/// </summary>
public static class StartIntakeEndpoint
{
    public sealed record StartIntakeRequest(Guid ProviderId, string Email);

    public static IEndpointRouteBuilder MapStartIntakeEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/intakes",
            async (StartIntakeRequest body, IMediator mediator, CancellationToken ct) =>
        {
            if (body is null || body.ProviderId == Guid.Empty)
                return ProblemResults.EmptyProviderId();
            if (string.IsNullOrWhiteSpace(body.Email))
                return ProblemResults.InvalidIntakeStart("email is required.");

            try
            {
                var result = await mediator.Send(
                    new StartIntakeCommand(body.ProviderId, body.Email), ct);
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
            catch (ArgumentException ex) when (ex.ParamName == "request")
            {
                return ProblemResults.InvalidIntakeStart(ex.Message);
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
