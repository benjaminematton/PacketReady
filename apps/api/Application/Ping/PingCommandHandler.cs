using System.Diagnostics;
using System.Text.Json;
using MediatR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PacketReady.Application.Abstractions;
using PacketReady.Application.Audit;
using PacketReady.Domain.Audit;

namespace PacketReady.Application.Ping;

public sealed class PingCommandHandler : IRequestHandler<PingCommand, PingResult>
{
    // Single ActivitySource per process. The OTel listener in Program.cs subscribes to
    // it by name; tests can subscribe via ActivityListener for assertion.
    private static readonly ActivitySource ActivitySource = new("PacketReady");

    private const string ModelId = "claude-haiku-4-5";

    // Haiku 4.5 published pricing as of 2026-05: $1/MTok input, $5/MTok output.
    // Phase 0 hard-codes a single model; replace with a per-model rate table when a
    // second model lands.
    private const decimal InputPerMTokUsd = 1.0m;
    private const decimal OutputPerMTokUsd = 5.0m;

    private readonly IChatClient _chat;
    private readonly IAuditWriter _audit;
    private readonly IUnitOfWork _uow;
    private readonly ILogger<PingCommandHandler> _logger;

    public PingCommandHandler(
        IChatClient chat,
        IAuditWriter audit,
        IUnitOfWork uow,
        ILogger<PingCommandHandler> logger)
    {
        _chat = chat;
        _audit = audit;
        _uow = uow;
        _logger = logger;
    }

    public async Task<PingResult> Handle(PingCommand request, CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity("ping.invoke", ActivityKind.Client);
        activity?.SetTag("langfuse.observation.input", request.Message);
        activity?.SetTag("gen_ai.request.model", ModelId);

        var messages = new List<ChatMessage> { new(ChatRole.User, request.Message) };
        var options = new ChatOptions
        {
            ModelId = ModelId,
            MaxOutputTokens = 256,
            Temperature = 0.7f,
        };

        var response = await _chat.GetResponseAsync(messages, options, ct);
        var reply = response.Text ?? string.Empty;

        var inputTokens = (int)(response.Usage?.InputTokenCount ?? 0);
        var outputTokens = (int)(response.Usage?.OutputTokenCount ?? 0);
        var costUsd =
            (inputTokens / 1_000_000m) * InputPerMTokUsd +
            (outputTokens / 1_000_000m) * OutputPerMTokUsd;

        activity?.SetTag("langfuse.observation.output", reply);
        activity?.SetTag("gen_ai.usage.input_tokens", inputTokens);
        activity?.SetTag("gen_ai.usage.output_tokens", outputTokens);

        var payload = JsonSerializer.Serialize(new
        {
            request = request.Message,
            reply,
            model = ModelId,
            input_tokens = inputTokens,
            output_tokens = outputTokens,
            cost_usd = costUsd,
        });

        var evt = AuditEvent.Create(AuditEventType.PingExecuted, payload);
        var auditId = _audit.Stage(evt);
        await _uow.SaveChangesAsync(ct);

        var traceId = activity?.TraceId.ToHexString() ?? string.Empty;

        _logger.LogInformation(
            "Ping ok: model={Model} in={In} out={Out} cost=${Cost} audit={Audit}",
            ModelId, inputTokens, outputTokens, costUsd, auditId);

        return new PingResult(reply, ModelId, auditId, traceId, inputTokens, outputTokens, costUsd);
    }
}
