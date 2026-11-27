using System.Text.Json;
using System.Text.Json.Serialization;

namespace VMCrate.Core;

public enum AssistantProvider { ClaudeCode, Codex, DeepSeekHarness }

public sealed record AssistantRequest(string Prompt, string Schema);
public sealed record AssistantFact(string Id, string Text);
public sealed record AssistantFinding(string Text, string[] Evidence);
public sealed record AssistantExplanation(AssistantFinding[] Findings, string NextCheck);

public interface IAssistantClient
{
    Task<string> CompleteAsync(AssistantRequest request, CancellationToken cancellation);
}

internal static class AssistantJson
{
    internal static readonly JsonSerializerOptions Strict = new(JsonDefaults.Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        MaxDepth = 20
    };

    internal static T Read<T>(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply) || reply.Length > 128 * 1024)
            throw new LabException("error.aiResponse");
        var json = reply.Trim();
        var firstLine = json.IndexOf('\n');
        if (firstLine >= 0 && json[..firstLine].TrimEnd().Equals("```json", StringComparison.OrdinalIgnoreCase) &&
            json.EndsWith("```", StringComparison.Ordinal))
            json = json[(firstLine + 1)..^3];
        try
        {
            return JsonSerializer.Deserialize<T>(json, Strict) ?? throw new LabException("error.aiResponse");
        }
        catch (JsonException error)
        {
            // Provider output can contain private context. Keep it out of persistent error details.
            throw new LabException("error.aiResponse", inner: error);
        }
    }

    internal static bool TextField(string? value, int max) => value is not null && value.Length <= max &&
        !value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'));
}
