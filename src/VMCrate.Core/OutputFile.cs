namespace VMCrate.Core;

public static class OutputFile
{
    public static void WriteNew(string path, string extension, Action<Stream> write) =>
        WriteNewAsync(path, extension, stream =>
        {
            write(stream);
            return Task.CompletedTask;
        }).GetAwaiter().GetResult();

    public static async Task WriteNewAsync(string path, string extension, Func<Stream, Task> write)
    {
        var destination = LocalPaths.FullPath(path);
        if (!string.Equals(Path.GetExtension(destination), extension, StringComparison.OrdinalIgnoreCase))
            throw new LabException("error.file", destination);
        LocalPaths.RequireOrdinaryPath(destination);
        if (File.Exists(destination))
            throw new LabException("error.outputExists", destination);

        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".vmcrate-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await write(output).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination);
        }
        catch (IOException error) when (File.Exists(destination))
        {
            throw new LabException("error.outputExists", destination, error);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
