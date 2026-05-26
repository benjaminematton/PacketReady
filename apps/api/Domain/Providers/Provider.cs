using System.Text.Json;

namespace PacketReady.Domain.Providers;

/// <summary>
/// Durable provider record. Phase 1 carries just <see cref="Id"/>, <see cref="CreatedAt"/>,
/// and the serialized <see cref="ProviderProfile"/>. Phase 5 will add <c>Email</c>,
/// intake-session FSM state, and outbound-message linkage.
///
/// <para>The profile lives as a JSONB blob — Phase 1 has no extraction layer to populate
/// structured columns, and Phase 3 will rebuild the profile from extraction rows rather
/// than promoting fields. Keeping the column shape stable across that transition.</para>
/// </summary>
public class Provider
{
    /// <summary>
    /// Default payer assigned to providers created without an explicit
    /// <c>payerId</c>. Matches the YAML filename
    /// <c>apps/api/Infrastructure/Payers/payers/payer-a-national-hmo.yaml</c>
    /// (added in P4 task 7). Kept as a constant so the seed CLI, tests, and
    /// the EF column default all agree.
    /// </summary>
    public const string DefaultPayerId = "payer-a-national-hmo";

    /// <summary>
    /// Per-provider cap on agent turns inside an intake session. Default
    /// from phase-5-intake-agent.md (decision table: "8 turns total
    /// before forced escalation"). Tunable on a per-provider basis when
    /// (e.g.) a particularly complex specialty needs more rounds; the
    /// default catches "agent never decides it's done" runaway after the
    /// 8th turn.
    /// </summary>
    public const int DefaultIntakeBudgetTurns = 8;

    public Guid Id { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Identifier of the payer whose YAML requirements this provider's
    /// extractions are validated against. P4 introduces it; admin-level
    /// payer selection at intake lands in P5. Defaults to
    /// <see cref="DefaultPayerId"/>. The string is opaque to the domain —
    /// resolution to a payer-config record happens in
    /// <c>PayerRequirementLoader</c>, which fails-loud at startup if the
    /// id isn't backed by a YAML file.
    /// </summary>
    public string PayerId { get; private set; } = DefaultPayerId;

    /// <summary>
    /// Per-provider cap on total agent turns in this intake. Set at
    /// <see cref="Create"/> time (admin-supplied or
    /// <see cref="DefaultIntakeBudgetTurns"/>) and immutable thereafter
    /// — no mutator is exposed today. The value is copied into
    /// <see cref="IntakeSession.TurnBudget"/> at session start; the
    /// snapshot is a forward guarantee so that if a setter is added
    /// later, running intakes won't see a moving target.
    /// </summary>
    public int IntakeBudgetTurns { get; private set; } = DefaultIntakeBudgetTurns;

    private string _profileJson = "{}";

    /// <summary>
    /// Cached deserialized profile. Not mapped — invalidated whenever the EF-driven
    /// <see cref="ProfileJson"/> setter reassigns. Domain entities are single-threaded
    /// per request (one DbContext, one scope), so no lock is needed.
    /// </summary>
    private ProviderProfile? _profileCache;

    /// <summary>
    /// JSON-serialized <see cref="ProviderProfile"/> using <see cref="DomainJson.Options"/>.
    /// Shape validated by <see cref="ProviderProfile.Validate"/> in <see cref="Create"/>.
    /// Default <c>"{}"</c> exists for EF materialization.
    /// </summary>
    public string ProfileJson
    {
        get => _profileJson;
        private set
        {
            _profileJson = value;
            _profileCache = null;
        }
    }

    private Provider() { }

    public static Provider Create(
        ProviderProfile profile,
        DateTimeOffset? now = null,
        string? payerId = null,
        int? intakeBudgetTurns = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var createdAt = now ?? DateTimeOffset.UtcNow;

        // Fail fast at the write boundary, not when a validator runs. A bad NPI or a
        // future DOB would otherwise surface as a misleading downstream Issue.
        // Defense-in-depth even when callers used ProviderProfile.Create: covers
        // `with`-mutated profiles and JSONB-deserialized profiles.
        ProviderProfile.Validate(profile, createdAt);

        // null = "caller omitted, use default"; "" / "   " = caller bug, fail loud
        // instead of silently routing to payer-a.
        if (payerId is not null && string.IsNullOrWhiteSpace(payerId))
            throw new ArgumentException(
                "payerId must be null (to use the default) or a non-whitespace value.",
                nameof(payerId));

        var resolvedPayerId = payerId ?? DefaultPayerId;

        // null = use the default; anything <= 0 is a caller bug. The
        // aggregate refuses 0 / negative anyway (IntakeSession.Start's
        // turnBudget invariant), but failing here points at the
        // create-provider call site instead of at SaveChanges.
        if (intakeBudgetTurns is { } budget && budget < 1)
            throw new ArgumentOutOfRangeException(
                nameof(intakeBudgetTurns), budget,
                "intakeBudgetTurns must be null (to use the default) or >= 1.");

        return new Provider
        {
            Id = Guid.NewGuid(),
            CreatedAt = createdAt,
            PayerId = resolvedPayerId,
            IntakeBudgetTurns = intakeBudgetTurns ?? DefaultIntakeBudgetTurns,
            ProfileJson = JsonSerializer.Serialize(profile, DomainJson.Options),
        };
    }

    /// <summary>
    /// Test-only factory: identical to <see cref="Create"/> but lets the
    /// caller pin <see cref="Id"/>. Tests need a fixed provider id so an
    /// in-memory seed lines up with a constant the test asserts against
    /// (e.g. seeding an <c>IntakeSession</c> with a known
    /// <c>ProviderId</c> before invoking <see cref="Provider.Create"/>'s
    /// generated id would have to be threaded back out). Production code
    /// must keep using <see cref="Create"/>; the
    /// <see cref="EditorBrowsableAttribute"/> hint keeps this off the
    /// IntelliSense menu in app code.
    /// </summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static Provider CreateForTesting(
        Guid id,
        ProviderProfile profile,
        DateTimeOffset? now = null,
        string? payerId = null,
        int? intakeBudgetTurns = null)
    {
        var p = Create(profile, now, payerId, intakeBudgetTurns);
        p.Id = id;
        return p;
    }

    /// <summary>
    /// Deserialized profile, cached after the first call. Cache invalidates if EF
    /// rehydrates the entity (the <see cref="ProfileJson"/> setter clears it).
    /// </summary>
    public ProviderProfile GetProfile() =>
        _profileCache ??= ProviderProfile.FromJson(_profileJson, Id);
}
