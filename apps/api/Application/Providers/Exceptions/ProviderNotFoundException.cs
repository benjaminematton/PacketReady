namespace PacketReady.Application.Providers.Exceptions;

/// <summary>
/// Thrown when a provider id does not match any provider row. The API layer
/// catches this and maps to a 404 — handlers stay HTTP-agnostic. Lives here
/// (not under any single feature folder) because the aggregator, score handler,
/// upload handler, and provider-detail query all surface it.
/// </summary>
public sealed class ProviderNotFoundException(Guid providerId)
    : Exception($"Provider {providerId} not found.")
{
    public Guid ProviderId { get; } = providerId;
}
