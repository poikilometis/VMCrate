using System.Text.Json.Serialization;

namespace VMCrate.Core;

public enum OutboxStatus { Writing, Sealed, Received, Archived }

public sealed record OutboxTransfer(Guid Id, Guid Source, Guid Receiver, string SourceName, string ReceiverName,
    OutboxStatus Status, long CapacityBytes, DateTimeOffset CreatedUtc, FileEvidence? Evidence = null, FileEvidence[]? DeclaredFiles = null)
{
    [JsonIgnore]
    internal Guid? Attachment => Status switch
    {
        OutboxStatus.Writing => Source,
        OutboxStatus.Sealed or OutboxStatus.Received => Receiver,
        _ => null
    };

    internal bool Uses(Guid id) => Status != OutboxStatus.Archived && (Source == id || Receiver == id);

    internal static bool ValidClaims(IReadOnlyList<FileEvidence>? claims, long capacity) =>
        claims is null || (claims.Count <= 128 && claims.All(item => item is not null &&
            item.Name is { Length: > 0 and <= 255 } && item.Name != "." && item.Name != ".." &&
            item.Name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !item.Name.Any(char.IsControl) &&
            item.SizeBytes >= 0 && item.SizeBytes <= capacity &&
            item.Sha256 is { Length: 64 } && item.Sha256.All(char.IsAsciiHexDigit)) &&
            claims.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == claims.Count &&
            claims.Sum(item => item.SizeBytes) <= capacity);

    internal static bool ValidCatalog(List<OutboxTransfer>? items) => items is { Count: <= 128 } &&
        items.All(item => item is not null && item.Id != Guid.Empty && item.Source != Guid.Empty && item.Receiver != Guid.Empty &&
            item.Source != item.Receiver && Enum.IsDefined(item.Status) && item.CapacityBytes is >= (64L << 20) and <= (8L << 30) &&
            ValidClaims(item.DeclaredFiles, item.CapacityBytes)) &&
        items.Select(item => item.Id).Distinct().Count() == items.Count &&
        items.Where(item => item.Status != OutboxStatus.Archived).SelectMany(item => new[] { item.Source, item.Receiver })
            .GroupBy(id => id).All(group => group.Count() == 1);
}
