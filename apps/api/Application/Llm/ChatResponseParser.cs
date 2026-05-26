using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PacketReady.Application.Llm;

/// <summary>
/// Shared <see cref="ChatResponse"/> handling for every LLM-backed caller
/// across both layers — Infrastructure's extractors and classifier, and
/// Application's P4 LLM validators (<c>IdentityCoherenceValidator</c>,
/// <c>NpiTaxonomyMatchValidator</c>). Lives in Application so validators
/// don't have to invert the layer relationship to reuse it.
///
/// <para><see cref="ChatResponseFormat.ForJsonSchema"/> is implemented as a
/// forced tool call on the Anthropic adapter, so <see cref="FunctionCallContent"/>
/// is the load-bearing path. <see cref="ChatResponse.Text"/> is a fallback
/// for adapter versions that surface the JSON as plain text — and for
/// versions that emit both (text wrapper + tool call), the function call
/// wins so we never feed the wrapper into <c>JsonDocument.Parse</c>.</para>
/// </summary>
public static class ChatResponseParser
{
    public static string ExtractStructuredJson(ChatResponse response)
    {
        // First non-empty FunctionCallContent wins. The schema enforces a single
        // forced tool call; if a future adapter version surfaces multiple, the
        // additional calls are model misbehavior, not signal we want to merge.
        foreach (var msg in response.Messages)
        {
            foreach (var content in msg.Contents)
            {
                if (content is FunctionCallContent fc && fc.Arguments is { Count: > 0 } args)
                    return JsonSerializer.Serialize(args);
            }
        }

        return response.Text ?? string.Empty;
    }

    /// <summary>
    /// Truncates an LLM response payload for inclusion in an error message.
    /// 200 chars is enough to identify the shape of the failure (truncation
    /// marker, escape sequence, missing brace) without flooding logs.
    /// </summary>
    public static string TruncateForError(string s) =>
        s.Length <= 200 ? s : s[..200] + "…";
}
