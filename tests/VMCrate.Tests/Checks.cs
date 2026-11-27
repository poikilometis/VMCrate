using System.IO;
using VMCrate.Core;

namespace VMCrate.Tests;

internal static class Checks
{
    internal static Func<Task> Run(Action check) => () =>
    {
        check();
        return Task.CompletedTask;
    };

    internal static void Expect(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    internal static async Task ErrorAsync(string key, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (LabException error) when (error.Key == key)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {key}.");
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    internal string Root { get; } = Directory.CreateTempSubdirectory("vmcrate-test-").FullName;

    internal string File(string name, string contents)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(Root);
        if (Path.GetDirectoryName(full) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()) ||
            !Path.GetFileName(full).StartsWith("vmcrate-test-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected test directory.");

        foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            System.IO.File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(full, recursive: true);
    }
}
