using MediatR;

namespace PacketReady.Application.Ping;

/// <summary>
/// Phase 0 smoke command: single-turn Haiku call, writes one audit row, emits one
/// OTel span. Exists to validate the full DI graph end-to-end (MediatR → Anthropic
/// SDK → EF Core → OTel/Langfuse) before any product code lands.
/// </summary>
public record PingCommand(string Message) : IRequest<PingResult>;

/// <summary>
/// <paramref name="TraceId"/> is the OTel trace id (16-byte hex). Langfuse, configured
/// as the OTLP receiver, keys traces on this id post-ingest — the same value is the
/// trace's URL path in the Langfuse UI.
/// </summary>
public record PingResult(
    string Reply,
    string Model,
    Guid AuditEventId,
    string TraceId,
    int InputTokens,
    int OutputTokens,
    decimal CostUsd);
