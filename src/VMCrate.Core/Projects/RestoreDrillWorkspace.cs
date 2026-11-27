namespace VMCrate.Core;

internal sealed class RestoreDrillWorkspace : IDisposable
{
    internal const string DirectoryName = ".dr";
    private const string Ownership = "VMCrate offline restore drill v1";
    private readonly string workspace;
    private readonly FileStream ownership;

    internal string Root { get; }

    internal RestoreDrillWorkspace(string workspace)
    {
        this.workspace = workspace;
        Root = Path.Combine(workspace, DirectoryName);
        var id = Guid.Empty;
        var config = Path.Combine(Root, "machines", $"{id:N}", "hyperv", $"VMCrate-{id:N}", "Virtual Machines", $"{id:D}.vmcx");
        if (OperatingSystem.IsWindows() && config.Length >= 248)
            throw new LabException("error.drillPath", workspace);

        LocalPaths.MakeDirectory(workspace);
        var path = Path.Combine(workspace, "restore-drill.lock");
        LocalPaths.RequireOrdinaryPath(path);
        try { ownership = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
        {
            throw new LabException("error.busy", inner: error);
        }
        try
        {
            LocalPaths.RequireOrdinaryPath(Root);
            if (Directory.Exists(Root))
                VerifyOwner();
            else
            {
                LocalPaths.MakeDirectory(Root);
                using var output = new FileStream(Marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                output.Write(System.Text.Encoding.ASCII.GetBytes(Ownership));
                output.Flush(flushToDisk: true);
            }
        }
        catch
        {
            ownership.Dispose();
            throw;
        }
    }

    private string Marker => Path.Combine(Root, ".vmcrate-restore-drill");

    internal void Remove()
    {
        VerifyOwner();
        LocalPaths.RemoveRestoreDrillDirectory(workspace);
    }

    private void VerifyOwner()
    {
        LocalPaths.RequireOrdinaryPath(Marker);
        if (!File.Exists(Marker) || new FileInfo(Marker).Length != Ownership.Length || File.ReadAllText(Marker) != Ownership)
            throw new LabException("error.outputExists", Root);
    }

    public void Dispose() => ownership.Dispose();
}
