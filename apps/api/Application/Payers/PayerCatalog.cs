namespace PacketReady.Application.Payers;

/// <summary>
/// Default <see cref="IPayerCatalog"/> implementation backed by an in-memory
/// dictionary loaded once at DI bootstrap. The dictionary is supplied by
/// infrastructure's <c>PayerRequirementLoader.LoadAll</c>; the catalog itself
/// is layering-neutral so the test suite can construct one from an in-memory
/// dict without touching the loader.
/// </summary>
public sealed class PayerCatalog : IPayerCatalog
{
    private readonly IReadOnlyDictionary<string, PayerRequirement> _byId;

    public PayerCatalog(IReadOnlyDictionary<string, PayerRequirement> byId)
    {
        _byId = byId ?? throw new ArgumentNullException(nameof(byId));
    }

    public PayerRequirement Get(string id)
    {
        if (_byId.TryGetValue(id, out var requirement))
            return requirement;
        throw new PayerNotConfiguredException(id, _byId.Keys);
    }

    public bool TryGet(string id, out PayerRequirement requirement)
    {
        if (_byId.TryGetValue(id, out var found))
        {
            requirement = found;
            return true;
        }
        requirement = null!;
        return false;
    }

    public IReadOnlyCollection<string> Ids => (IReadOnlyCollection<string>)_byId.Keys;
}
