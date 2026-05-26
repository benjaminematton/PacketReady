namespace PacketReady.Application.Payers;

/// <summary>
/// One payer's credentialing requirements, hydrated from
/// <c>Payers/payers/{id}.yaml</c> at app startup by
/// <see cref="PayerRequirementLoader"/>. Resolved against
/// <c>Provider.PayerId</c> when the payer-aware validators run.
///
/// <para>This record IS the YAML schema — there's no separate schema document
/// to keep in sync. Add a field here and the YAML files; remove a field here
/// and the YAML files; YamlDotNet binds by property name (camelCase via the
/// loader's naming convention). The loader's shape validation is the only
/// place that enforces "required" — record properties have permissive
/// defaults so missing keys fail with a named, file-pinned error message
/// rather than a stack trace.</para>
///
/// <para><c>string[]</c> (rather than <c>List&lt;string&gt;</c>) is deliberate:
/// the singleton dictionary is loaded once at DI bootstrap and shared by every
/// validator, so the collection mutation surface should be as small as
/// possible. Arrays block <c>.Add()</c>/<c>.Remove()</c>/<c>.Clear()</c>;
/// element reassignment is theoretical and not worth the two-step-parse
/// overhead of <c>IReadOnlyList&lt;string&gt;</c> (YamlDotNet won't bind to
/// the read-only interface directly).</para>
/// </summary>
public sealed record PayerRequirement
{
    /// <summary>
    /// Stable payer identifier. Must equal the YAML file's basename
    /// (e.g. <c>payer-a-national-hmo.yaml</c> → <c>"payer-a-national-hmo"</c>).
    /// Loader enforces.
    /// </summary>
    public string Id { get; init; } = "";

    /// <summary>Human-readable label used in Issue messages and admin UI.</summary>
    public string Name { get; init; } = "";

    /// <summary>Minimum acceptable malpractice coverage; consumed by the
    /// malpractice currency validator.</summary>
    public MalpracticeRequirement Malpractice { get; init; } = new();

    /// <summary>
    /// Document types this payer requires. Strings match the camelCase
    /// <c>docType</c> form (<c>license</c>, <c>dea</c>, <c>boardCert</c>,
    /// <c>malpractice</c>, plus any future payer-specific types). The
    /// required-docs validator skips types the aggregator's universal-4
    /// floor already covers — see
    /// <c>docs/impl/phase-4-scale-and-llm-validators.md</c> "Missing-doc
    /// ownership split".
    /// </summary>
    public string[] RequiredDocuments { get; init; } = [];

    /// <summary>
    /// If <c>false</c>, the board-cert validator suppresses the missing-cert
    /// Critical (no Issue emitted at all). Some payers — notably Medicaid
    /// plans — don't require board cert.
    /// </summary>
    public bool BoardCertRequired { get; init; }

    /// <summary>
    /// Accepted board acronyms (<c>"ABMS"</c>, <c>"ABIM"</c>, ...). Empty
    /// means "any board is fine"; populated means the board-cert validator
    /// emits Major when the extracted board isn't on the list. Loader
    /// rejects both asymmetries — <c>BoardCertRequired=true</c> with an empty
    /// list, and <c>BoardCertRequired=false</c> with a non-empty list — as
    /// misconfigs, so the data and the boolean stay in lockstep.
    /// </summary>
    public string[] AcceptedBoards { get; init; } = [];

    /// <summary>Renewal-window thresholds (days before expiry) consumed by
    /// the malpractice and license validators.</summary>
    public WindowDays WindowDays { get; init; } = new();
}

public sealed record MalpracticeRequirement
{
    /// <summary>Minimum per-occurrence coverage in whole dollars.</summary>
    public long MinimumPerOccurrence { get; init; }

    /// <summary>Minimum aggregate coverage in whole dollars. Must be
    /// &gt;= <see cref="MinimumPerOccurrence"/> (loader enforces) — aggregate
    /// is the policy-wide cap and can't sit below a single occurrence cap.</summary>
    public long MinimumAggregate { get; init; }
}

public sealed record WindowDays
{
    /// <summary>Days before malpractice expiry to start emitting a Minor
    /// renewal warning.</summary>
    public int MalpracticeRenewal { get; init; }

    /// <summary>Days before license expiry to start emitting a Minor
    /// renewal warning.</summary>
    public int LicenseRenewal { get; init; }
}
