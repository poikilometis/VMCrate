using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using VMCrate.Core;

namespace VMCrate.Windows;

internal sealed record CliOutput(int ExitCode, string Text, string Diagnostics);

internal static class CliProcess
{
    private const int OutputLimit = 1024 * 1024;

    internal static async Task<CliOutput> RunAsync(ProcessStartInfo start, string? message, TimeSpan limit,
        CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(limit);
        var utf8 = new UTF8Encoding(false);
        start.CreateNoWindow = true;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardInputEncoding = start.StandardOutputEncoding = start.StandardErrorEncoding = utf8;

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
                throw new LabException("error.aiCliMissing");
        }
        catch (Win32Exception error)
        {
            throw new LabException("error.aiCliMissing", inner: error);
        }

        using var stop = deadline.Token.Register(() => Terminate(process));
        var output = ReadAsync(process.StandardOutput, deadline);
        var errors = ReadAsync(process.StandardError, deadline);
        try
        {
            if (message is not null)
                await process.StandardInput.WriteAsync(message.AsMemory(), deadline.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            return new(process.ExitCode, await output, await errors);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new LabException("error.aiTimeout");
        }
        catch (IOException error)
        {
            cancellation.ThrowIfCancellationRequested();
            throw new LabException("error.aiCli", inner: error);
        }
        finally
        {
            Terminate(process);
            try { await Task.WhenAll(output, errors); }
            catch (Exception error) when (error is IOException or OperationCanceledException or LabException) { }
        }
    }

    private static async Task<string> ReadAsync(StreamReader reader, CancellationTokenSource deadline)
    {
        var result = new StringBuilder();
        var buffer = new char[8192];
        int count;
        while ((count = await reader.ReadAsync(buffer, deadline.Token)) > 0)
        {
            if (result.Length + count > OutputLimit)
            {
                deadline.Cancel();
                throw new LabException("error.aiResponse");
            }
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }

    private static void Terminate(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
    }
}
