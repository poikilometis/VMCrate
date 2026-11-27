using System.IO;
using System.Runtime.InteropServices;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed record SessionLaunch(Guid ProfileId, string Workspace, string[] Files)
{
    internal static SessionLaunch? Parse(string[] args)
    {
        if (args is not ["--session-profile", var id, var workspace, .. var files] ||
            !Guid.TryParseExact(id, "N", out var profile) || profile == Guid.Empty || !Path.IsPathFullyQualified(workspace))
            return null;
        return new(profile, LocalPaths.FullPath(workspace), files);
    }
}

internal static class SessionShortcut
{
    internal static void Save(string path, SessionProfile profile, string workspace)
    {
        var executable = LocalPaths.RequireFile(Path.Combine(AppContext.BaseDirectory, "VMCrate.exe"), ".exe");
        var directory = Path.GetDirectoryName(LocalPaths.FullPath(path))!;
        var root = Path.TrimEndingDirectorySeparator(LocalPaths.FullPath(workspace));
        LocalPaths.RequireOrdinaryPath(directory);
        var staging = Path.Combine(directory, $".vmcrate-{Guid.NewGuid():N}.lnk");
        object? shellObject = null;
        object? shortcutObject = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new LabException("error.profile");
            shellObject = Activator.CreateInstance(type) ?? throw new LabException("error.profile");
            dynamic shell = shellObject;
            shortcutObject = shell.CreateShortcut(staging);
            dynamic shortcut = shortcutObject;
            shortcut.TargetPath = executable;
            shortcut.WorkingDirectory = Path.GetDirectoryName(executable);
            shortcut.Arguments = $"--session-profile {profile.Id:N} \"{root}\"";
            shortcut.Save();
            OutputFile.WriteNew(path, ".lnk", output =>
            {
                using var input = File.OpenRead(staging);
                input.CopyTo(output);
            });
        }
        finally
        {
            if (shortcutObject is not null)
                Marshal.FinalReleaseComObject(shortcutObject);
            if (shellObject is not null)
                Marshal.FinalReleaseComObject(shellObject);
            File.Delete(staging);
        }
    }
}
