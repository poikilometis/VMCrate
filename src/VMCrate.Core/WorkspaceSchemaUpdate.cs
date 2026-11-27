using System.Security.Cryptography;
using System.Text.Json;

namespace VMCrate.Core;

public static class WorkspaceSchemaUpdate
{
    public static string DirectoryFor(string workspace, Guid id) => id == Guid.Empty
        ? throw new LabException("error.appUpdate")
        : Path.Combine(LocalPaths.FullPath(workspace), "app-updates", id.ToString("N"));

    public static void Prepare(string workspace, Guid id) => new StateStore(workspace).PrepareUpdate(id);
    public static void Rollback(string workspace, Guid id) => new StateStore(workspace).RollbackUpdate(id);
}

internal sealed partial class StateStore
{
    internal void PrepareUpdate(Guid id)
    {
        using var locked = Lock();
        var directory = WorkspaceSchemaUpdate.DirectoryFor(root, id);
        LocalPaths.RequireOrdinaryPath(directory);
        if (!Directory.Exists(directory))
            throw new LabException("error.appUpdate");
        var receiptPath = Path.Combine(directory, "schema.json");
        var previous = Path.Combine(directory, "state.before.json");
        LocalPaths.RequireOrdinaryPath(statePath);
        LocalPaths.RequireOrdinaryPath(receiptPath);
        LocalPaths.RequireOrdinaryPath(previous);

        var before = File.Exists(statePath) ? ReadStateBytes(statePath) : null;
        if (File.Exists(receiptPath))
        {
            var recorded = ReadMigration(receiptPath);
            var digest = Digest(before);
            if (digest == recorded.After)
                return;
            if (digest != recorded.Before)
                throw new LabException("error.appUpdateChanged");
        }
        var state = Load();
        var after = before is null || SchemaOf(before) == LabState.CurrentVersion
            ? before : JsonSerializer.SerializeToUtf8Bytes(state, JsonDefaults.Options);
        var receipt = new SchemaReceipt(Digest(before), Digest(after));
        if (before is not null && !File.Exists(previous))
            WriteAtomic(previous, before);
        if (Digest(File.Exists(previous) ? ReadStateBytes(previous) : null) != receipt.Before)
            throw new LabException("error.appUpdateChanged");
        WriteAtomic(receiptPath, JsonSerializer.SerializeToUtf8Bytes(receipt, JsonDefaults.Options));
        if (!ReferenceEquals(before, after))
            WriteAtomic(statePath, after!);
    }

    internal void RollbackUpdate(Guid id)
    {
        using var locked = Lock();
        var directory = WorkspaceSchemaUpdate.DirectoryFor(root, id);
        var receiptPath = Path.Combine(directory, "schema.json");
        var previous = Path.Combine(directory, "state.before.json");
        LocalPaths.RequireOrdinaryPath(statePath);
        LocalPaths.RequireOrdinaryPath(receiptPath);
        LocalPaths.RequireOrdinaryPath(previous);
        if (!File.Exists(receiptPath))
            throw new LabException("error.appUpdate");
        var receipt = ReadMigration(receiptPath);
        var current = File.Exists(statePath) ? ReadStateBytes(statePath) : null;
        var digest = Digest(current);
        if (digest == receipt.Before)
            return;
        if (current is null || digest != receipt.After || !File.Exists(previous))
            throw new LabException("error.appUpdateChanged");
        var before = ReadStateBytes(previous);
        if (Digest(before) != receipt.Before)
            throw new LabException("error.appUpdateChanged");
        WriteAtomic(Path.Combine(directory, "state.replaced.json"), current);
        WriteAtomic(statePath, before);
    }

    private static SchemaReceipt ReadMigration(string file) =>
        JsonSerializer.Deserialize<SchemaReceipt>(ReadStateBytes(file, 4096), AssistantJson.Strict)
        ?? throw new LabException("error.appUpdateChanged");

    private static byte[] ReadStateBytes(string path, int maximum = 64 * 1024 * 1024)
    {
        using var input = File.OpenRead(path);
        if (input.Length > maximum)
            throw new LabException("error.state", path);
        var bytes = new byte[(int)input.Length];
        input.ReadExactly(bytes);
        return bytes;
    }

    private static int SchemaOf(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.EnumerateObject()
            .Single(property => property.Name.Equals("version", StringComparison.OrdinalIgnoreCase)).Value.GetInt32();
    }

    private static string? Digest(byte[]? bytes) => bytes is null ? null : Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void WriteAtomic(string path, byte[] data)
    {
        LocalPaths.RequireOrdinaryPath(path);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private sealed record SchemaReceipt(string? Before, string? After);
}
