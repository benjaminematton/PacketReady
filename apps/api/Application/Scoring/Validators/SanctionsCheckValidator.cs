using PacketReady.Domain.Providers;
using PacketReady.Domain.Scoring;

namespace PacketReady.Application.Scoring.Validators;

/// <summary>
/// Checks OIG (Office of Inspector General) and SAM (System for Award Management)
/// sanctions/debarment status. The two sources are checked <b>independently</b>,
/// so the side-panel can cite whichever source failed (or both).
///
/// <list type="bullet">
///   <item>Critical — no sanctions check on file (one Issue; we don't know which
///         source is missing — the whole check is the gap).</item>
///   <item>Critical — <c>OigClean == false</c> (one Issue, cites OIG).</item>
///   <item>Critical — <c>SamClean == false</c> (one Issue, cites SAM).</item>
///   <item>Major — <c>today − CheckedAt ≥ 365 days</c> for a clean source. Emitted
///         per-source: one for OIG, one for SAM (since both share <c>CheckedAt</c>
///         today, both fire when staleness applies). Staleness on a non-clean
///         source is suppressed — the Critical "sanction on file" is the headline,
///         and the freshness of that finding doesn't change the remediation.</item>
///   <item>Minor — <c>90 ≤ today − CheckedAt &lt; 365 days</c> for a clean source.
///         Per-source, same logic.</item>
/// </list>
///
/// <para>The "stale" thresholds operate on a single <c>CheckedAt</c> shared by both
/// sources. Per-source emission is a UX choice (each source gets its own citation),
/// not a reflection of independent timestamps. The data model can be split later.</para>
/// </summary>
public sealed class SanctionsCheckValidator(TimeProvider clock) : IValidator
{
    public string Name => "sanctions_check";

    private const int MajorStalenessDays = 365;
    private const int MinorStalenessDays = 90;

    public Task<IReadOnlyList<Issue>> RunAsync(ProviderProfile profile, CancellationToken ct)
    {
        var issues = new List<Issue>();
        var nowUtc = clock.GetUtcNow();

        if (profile.Sanctions is null)
        {
            issues.Add(new Issue(
                Validator: Name,
                Severity: Severity.Critical,
                Message: "No sanctions check on file.",
                Remediation: "Run OIG and SAM exclusion lookups before submission.",
                Citations: Array.Empty<Citation>()));
            return Task.FromResult<IReadOnlyList<Issue>>(issues);
        }

        var s = profile.Sanctions;

        if (!s.OigClean)
            issues.Add(new Issue(Name, Severity.Critical,
                "OIG sanction on file.",
                "Provider has an active OIG exclusion; cannot be credentialed until resolved.",
                [new Citation(Name, $"OIG=excluded checked_at={s.CheckedAt:O}")]));

        if (!s.SamClean)
            issues.Add(new Issue(Name, Severity.Critical,
                "SAM debarment on file.",
                "Provider has an active SAM debarment; cannot be credentialed until resolved.",
                [new Citation(Name, $"SAM=debarred checked_at={s.CheckedAt:O}")]));

        // Staleness only applies to clean sources — see XML doc above for why.
        // (int) truncates toward zero — a check 89.9 days old reads as 89.
        // Acceptable: CheckedAt is a real timestamp; sub-day jitter is noise.
        var staleness = nowUtc - s.CheckedAt;
        var stalenessDays = (int)staleness.TotalDays;

        if (stalenessDays >= MajorStalenessDays)
        {
            if (s.OigClean) issues.Add(StaleIssue("OIG", Severity.Major, stalenessDays, s.CheckedAt));
            if (s.SamClean) issues.Add(StaleIssue("SAM", Severity.Major, stalenessDays, s.CheckedAt));
        }
        else if (stalenessDays >= MinorStalenessDays)
        {
            if (s.OigClean) issues.Add(StaleIssue("OIG", Severity.Minor, stalenessDays, s.CheckedAt));
            if (s.SamClean) issues.Add(StaleIssue("SAM", Severity.Minor, stalenessDays, s.CheckedAt));
        }

        return Task.FromResult<IReadOnlyList<Issue>>(issues);
    }

    private Issue StaleIssue(string source, Severity severity, int days, DateTimeOffset checkedAt)
    {
        var (message, remediation) = severity == Severity.Major
            ? ($"{source} check is over a year old ({days} days).",
               $"Re-run the {source} exclusion lookup before payer submission.")
            : ($"{source} check is stale ({days} days since last lookup).",
               $"Re-run the {source} exclusion lookup; sources may have updated.");

        // Stale only fires for clean sources, so the verdict is "clean" by construction —
        // making it explicit keeps the citation shape symmetric with the dirty branches.
        return new Issue(Name, severity, message, remediation,
            [new Citation(Name, $"{source}=clean checked_at={checkedAt:O}")]);
    }
}
