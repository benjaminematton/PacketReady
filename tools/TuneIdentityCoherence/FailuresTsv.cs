using System.Text;

namespace PacketReady.TuneIdentityCoherence;

/// <summary>
/// Per-failure TSV writer. One row per (packet × emitted-issue) that the
/// human needs to categorize before the next iteration. The <c>category</c>
/// column is left empty — the human fills it in by hand-editing the TSV;
/// <c>category_counts.py</c> then aggregates across iteration TSVs to print
/// the trend table with Δ-vs-baseline counts per category.
///
/// <para>Format (tab-delimited): <c>iteration\tpacket_id\tfailure_type\tllm_message\tcategory</c></para>
///
/// <para>Failure type vocabulary:
/// <list type="bullet">
///   <item><c>fp_on_clean</c> — validator emitted on a packet with no planted markers.</item>
///   <item><c>fp_on_dont_flag</c> — validator emitted on a planted marker with
///   <c>expected_to_flag=false</c> (e.g. <c>SURNAME_TYPO</c>).</item>
///   <item><c>missed_should_flag</c> — validator did NOT emit on a planted marker
///   with <c>expected_to_flag=true</c>.</item>
/// </list></para>
/// </summary>
public static class FailuresTsv
{
    public static string Write(
        IReadOnlyList<FailureRow> failures,
        string tuningRunsRoot,
        int iteration)
    {
        Directory.CreateDirectory(tuningRunsRoot);
        var path = Path.Combine(tuningRunsRoot, $"iter-{iteration:00}__failures.tsv");

        var sb = new StringBuilder();
        sb.AppendLine("iteration\tpacket_id\tfailure_type\tllm_message\tcategory");
        foreach (var f in failures)
        {
            // TSV escaping: tabs and newlines in the LLM message would break
            // column alignment. Sanitize by stripping; a verbose message that
            // contained literal tabs is a model-output curiosity, not a tuning
            // signal we want to preserve byte-for-byte.
            var msg = (f.LlmMessage ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
            sb.Append(iteration).Append('\t')
              .Append(f.PacketId).Append('\t')
              .Append(f.FailureType).Append('\t')
              .Append(msg).Append('\t')
              .AppendLine();  // empty category column for human fill-in
        }
        File.WriteAllText(path, sb.ToString());
        return path;
    }
}

public sealed record FailureRow(
    string PacketId,
    string FailureType,
    string? LlmMessage);
