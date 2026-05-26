using Microsoft.Extensions.AI;

namespace PacketReady.TuneIdentityCoherence;

/// <summary>
/// <see cref="IChatClient"/> decorator that records per-call input/output token
/// counts so the tuning iteration log can quote real Sonnet usage without
/// changing the <see cref="PacketReady.Application.Scoring.Validators.IdentityCoherenceValidator"/>
/// interface (which would ripple through Application's <c>IValidator</c>
/// contract and every downstream consumer for a one-CLI need).
///
/// <para>Each <see cref="GetResponseAsync(IEnumerable{ChatMessage}, ChatOptions, CancellationToken)"/>
/// call pushes one <see cref="CallUsage"/> entry; the CLI snapshots the
/// counts immediately after the validator returns. Streaming isn't used by
/// the validator path so the streaming overload defers to the inner client
/// without counting — flag (don't silently undercount) if a future caller
/// wires it up.</para>
/// </summary>
internal sealed class CountingChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly List<CallUsage> _calls = new();

    public CountingChatClient(IChatClient inner)
    {
        _inner = inner;
    }

    public IReadOnlyList<CallUsage> Calls => _calls;

    public void Reset() => _calls.Clear();

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await _inner.GetResponseAsync(messages, options, cancellationToken);
        _calls.Add(new CallUsage(
            InputTokens: response.Usage?.InputTokenCount ?? 0,
            OutputTokens: response.Usage?.OutputTokenCount ?? 0));
        return response;
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => _inner.GetStreamingResponseAsync(messages, options, cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null)
        => _inner.GetService(serviceType, serviceKey);

    public void Dispose() => _inner.Dispose();
}

internal sealed record CallUsage(long InputTokens, long OutputTokens);
