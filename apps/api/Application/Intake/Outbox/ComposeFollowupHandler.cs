namespace PacketReady.Application.Intake.Outbox;

/// <summary>
/// Pure-code spine of the <c>compose_followup</c> tool. Aggregates a gap
/// list into one consolidated email — subject + body — rather than firing
/// per-gap template reminders (design.md §7.5 "Why consolidated
/// follow-ups").
///
/// <para>The agent supplies the gap list; this handler formats it. No
/// LLM call here — the agent already did the reasoning about what to
/// ask for. Keeping the formatting deterministic means a re-issue (or a
/// rewind + replay) produces the same email.</para>
/// </summary>
public sealed class ComposeFollowupHandler
{
    public sealed record Gap(string Kind, string Message, string? RemediationHint = null);

    public sealed record Followup(string Subject, string Body);

    /// <summary>
    /// Compose a single email for the gap list. Sorted by Kind for stable
    /// output (a deterministic compose lets two agent runs with the same
    /// gap set produce byte-identical bodies — useful for replay safety
    /// and for telemetry diffing).
    /// </summary>
    public Followup Compose(string providerFullName, IReadOnlyList<Gap> gaps)
    {
        if (string.IsNullOrWhiteSpace(providerFullName))
            providerFullName = "there";   // graceful when no profile name on file yet

        if (gaps is null || gaps.Count == 0)
            throw new ArgumentException(
                "Compose requires at least one gap. The agent should invoke compute_readiness instead when no gaps remain.",
                nameof(gaps));

        var sorted = gaps
            .OrderBy(g => g.Kind, StringComparer.Ordinal)
            .ToList();

        var subject = gaps.Count == 1
            ? $"PacketReady — one more thing to wrap your intake"
            : $"PacketReady — {gaps.Count} items left on your intake";

        var body = new System.Text.StringBuilder();
        body.Append("Hi ").Append(providerFullName).Append(",\n\n");
        body.Append("We're close to wrapping up your credentialing intake. ");
        body.Append(gaps.Count == 1
            ? "There's one item left:\n\n"
            : $"There are {gaps.Count} items left:\n\n");

        for (var i = 0; i < sorted.Count; i++)
        {
            var g = sorted[i];
            body.Append(i + 1).Append(". ").Append(g.Message);
            if (!string.IsNullOrWhiteSpace(g.RemediationHint))
                body.Append(" (").Append(g.RemediationHint).Append(")");
            body.Append('\n');
        }

        body.Append("\nUse the same link you got from us before to upload anything missing — it stays valid until you submit again. Thanks!");

        return new Followup(subject, body.ToString());
    }
}
