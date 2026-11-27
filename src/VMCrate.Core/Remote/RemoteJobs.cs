using System.Text.Json;
using System.Text.Json.Serialization;

namespace VMCrate.Core;

public enum RemoteLabAction { Deploy = 1, Start, PowerOff }
public enum RemoteJobStatus { Queued = 1, Running, Succeeded, Failed, Interrupted }

public sealed record RemoteRecipe(string Id, string Name, int Version, BlueprintResources Resources);
public sealed record RemoteRequest(Guid Id, string Recipe, RemoteLabAction Action);
public sealed record RemoteJob(Guid Id, string Recipe, RemoteLabAction Action, RemoteJobStatus Status,
    DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc, string? Error = null);
public sealed record RemoteFailure(string Code);

public static class RemoteProtocol
{
    public const string Header = "X-VMCrate-Queue";
    public const string Version = "1";
    public const string Prefix = "/api/v1";
    public static JsonSerializerOptions Json { get; } = new(JsonDefaults.Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false
    };
}
