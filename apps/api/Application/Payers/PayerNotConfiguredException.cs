namespace PacketReady.Application.Payers;

/// <summary>
/// Thrown when a <c>Provider.PayerId</c> does not match any payer YAML loaded
/// at startup. The API layer catches this and maps to a 4xx (operator bug —
/// either the provider row drifted from the deployed payer set, or the YAML
/// wasn't deployed). Raw <see cref="KeyNotFoundException"/> from a dictionary
/// lookup would surface as an opaque 500.
///
/// <para>Sibling of <c>ProviderNotFoundException</c>; same pattern, same
/// layering rationale.</para>
/// </summary>
public sealed class PayerNotConfiguredException : Exception
{
    public string PayerId { get; }
    public IReadOnlyCollection<string> KnownPayerIds { get; }

    public PayerNotConfiguredException(string payerId, IEnumerable<string> knownPayerIds)
        : this(payerId, knownPayerIds.ToList())
    {
    }

    private PayerNotConfiguredException(string payerId, IReadOnlyCollection<string> known)
        : base($"PayerId '{payerId}' is not backed by a YAML file. Known payers: [{string.Join(", ", known)}].")
    {
        PayerId = payerId;
        KnownPayerIds = known;
    }
}
