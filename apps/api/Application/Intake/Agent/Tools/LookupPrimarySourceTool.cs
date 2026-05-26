using System.Text.Json;
using PacketReady.Application.Intake.PrimarySources;

namespace PacketReady.Application.Intake.Agent.Tools;

/// <summary>
/// <c>lookup_primary_source</c> — verify the provider against an external
/// source (NPPES / OIG / SAM / state board / CAQH). P5 ships with a
/// canned mock; real PSV is post-launch. Side-effecting tool — replay
/// safety lives in the runtime's <c>primary_source_results</c> cache
/// (design.md §7.9), not here.
///
/// <para><b>Input:</b> <c>{ source, identifiers }</c><br/>
/// <b>Output:</b> <c>{ found, fields, mismatch_fields }</c></para>
/// </summary>
public sealed class LookupPrimarySourceTool : IIntakeTool
{
    public string Name => "lookup_primary_source";

    public string Description =>
        "Verify the provider against an external primary source (NPPES, OIG, SAM, state_board, CAQH). Returns whether the source has a record and which fields disagree with what we hold. Mocked in v1; real PSV is post-launch.";

    private static readonly JsonElement _schema = JsonDocument.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["source", "identifiers"],
          "properties": {
            "source": {
              "type": "string",
              "enum": ["nppes", "oig", "sam", "state_board", "caqh"]
            },
            "identifiers": {
              "type": "object",
              "description": "Source-specific. Typical shape: { npi, license_number?, state? }.",
              "additionalProperties": true
            }
          }
        }
        """).RootElement;

    public JsonElement InputSchema => _schema;

    private readonly IPrimarySourceLookup _lookup;

    public LookupPrimarySourceTool(IPrimarySourceLookup lookup) { _lookup = lookup; }

    public async Task<JsonElement> InvokeAsync(
        JsonElement args,
        Guid providerId,
        Guid turnId,
        CancellationToken ct)
    {
        if (!ToolArgs.TryReadString(args, "source", out var source))
            return ToolResults.Error("source is required.");
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("identifiers", out var identifiers)
            || identifiers.ValueKind != JsonValueKind.Object)
            return ToolResults.Error("identifiers is required and must be an object.");

        return await _lookup.LookupAsync(source, identifiers, ct);
    }
}
