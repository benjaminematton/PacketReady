using System.Text.Json;

namespace PacketReady.Application.Intake.Agent.Tools;

/// <summary>
/// Shared parsers + result builders for the 5 intake tools. Each tool
/// receives <see cref="JsonElement"/> args validated against its
/// <c>InputSchema</c> at the SDK boundary, but the LLM still emits weird
/// shapes occasionally (string-encoded UUIDs that aren't UUIDs, missing
/// optionals, etc.). These helpers centralize the "return a structured
/// error instead of throwing" pattern so each tool stays small.
/// </summary>
public static class ToolArgs
{
    public static bool TryReadGuid(JsonElement obj, string field, out Guid value)
    {
        value = Guid.Empty;
        return obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(field, out var el)
            && el.ValueKind == JsonValueKind.String
            && Guid.TryParse(el.GetString(), out value);
    }

    public static bool TryReadString(JsonElement obj, string field, out string value)
    {
        value = string.Empty;
        if (obj.ValueKind != JsonValueKind.Object) return false;
        if (!obj.TryGetProperty(field, out var el)) return false;
        if (el.ValueKind != JsonValueKind.String) return false;
        value = el.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    public static bool TryReadArray(JsonElement obj, string field, out JsonElement value)
    {
        value = default;
        return obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(field, out value)
            && value.ValueKind == JsonValueKind.Array;
    }
}

public static class ToolResults
{
    /// <summary>
    /// Build a <c>{ error: "..." }</c> result. The agent sees this in the
    /// next tool_result block and reasons about the failure — never throw
    /// from a tool unless the runtime should escalate.
    /// </summary>
    public static JsonElement Error(string message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { error = message });
        return JsonDocument.Parse(bytes).RootElement;
    }
}
