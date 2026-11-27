using System.Diagnostics;
using System.IO;
using VMCrate.Core;
using VMCrate.Windows;

namespace VMCrate.Desktop;

internal static class LocalJobProcess
{
    internal static async Task RunAsync(string workspace)
    {
        var root = WorkspaceVolume.Inspect(workspace).Directory;
        var host = new PowerShellHypervisor();
        var lab = new LabService(root, host, new UdfInboxWriter());
        await new LocalJobWorker(lab, new OfficialImages(new PublisherSignatureVerifier()), host.ReadHostReadinessAsync).RunAsync();
    }

    internal static void Enqueue(LabService lab, LocalJobRequest request)
    {
        new LocalJobQueue(lab.WorkspaceDirectory).Enqueue(request);
        Wake(lab.WorkspaceDirectory);
    }

    internal static void Wake(string root)
    {
        var file = LocalPaths.RequireFile(Path.Combine(AppContext.BaseDirectory, "VMCrate.exe"), ".exe");
        var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--local-jobs");
        start.ArgumentList.Add(root);
        using var worker = Process.Start(start) ?? throw new LabException("error.job");
    }

    internal static void WakePending(string root)
    {
        if (Directory.Exists(Path.Combine(root, "jobs")) && new LocalJobQueue(root).Read()
            .Any(job => job.Status is LocalJobStatus.Running or LocalJobStatus.Queued))
            Wake(root);
    }
}
