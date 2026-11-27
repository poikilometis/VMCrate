namespace VMCrate.Core;

internal sealed partial class StateStore
{
    internal string BackupPath => Path.Combine(root, "state.previous.json");

    internal LabState? ReadBackup()
    {
        try
        {
            LocalPaths.RequireOrdinaryPath(BackupPath);
            return File.Exists(BackupPath) ? Read(BackupPath) : null;
        }
        catch (Exception error) when (error is LabException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal void RestoreBackup(Guid expectedWorkspace)
    {
        try
        {
            _ = Load();
        }
        catch (LabException error) when (error.Key == "error.state")
        {
            var backup = ReadBackup();
            if (backup is null || backup.WorkspaceId != expectedWorkspace)
                throw new LabException("error.recoveryChanged");
            LocalPaths.RequireOrdinaryPath(statePath);
            var evidence = Path.Combine(root, $"state.failed-{Guid.NewGuid():N}.json");
            WriteState(backup, statePath, File.Exists(statePath) ? evidence : null);
            return;
        }
        throw new LabException("error.recoveryChanged");
    }
}
