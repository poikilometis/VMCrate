namespace VMCrate.Core;

public enum LocalJobKind { Build, Verify, Backup, Download, RestoreDrill }
public enum LocalJobStatus { Queued, Running, Succeeded, Failed, Cancelled }

public sealed record LocalJobRequest(LocalJobKind Kind, Guid Resource = default, BuildStage? Stage = null,
    string? Blueprint = null, string? Destination = null, string? Publisher = null, string? Digest = null)
{
    internal bool Valid => Kind switch
    {
        LocalJobKind.Build => Resource != Guid.Empty && Stage is { } stage && Enum.IsDefined(stage) && stage != BuildStage.Complete,
        LocalJobKind.Verify => Resource != Guid.Empty,
        LocalJobKind.Backup => Blueprint is { Length: >= 1 and <= 32 } && Destination is { Length: <= 2048 } folder && Path.IsPathFullyQualified(folder),
        LocalJobKind.Download => Publisher is { Length: >= 1 and <= 64 } && Digest is { Length: 64 } && Digest.All(char.IsAsciiHexDigit),
        LocalJobKind.RestoreDrill => Destination is { Length: <= 2048 } manifest && Path.IsPathFullyQualified(manifest),
        _ => false
    };
}

public sealed record LocalJob(Guid Id, LocalJobRequest Request, LocalJobStatus Status, DateTimeOffset UpdatedUtc,
    int Attempts = 0, bool CancelRequested = false, string? Failure = null, long? ProcessedBytes = null,
    long? TotalBytes = null, DateTimeOffset? StartedUtc = null, long? ElapsedMs = null);
