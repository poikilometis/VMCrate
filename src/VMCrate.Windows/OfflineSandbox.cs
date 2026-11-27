using System.Diagnostics;
using System.Xml.Linq;
using VMCrate.Core;

namespace VMCrate.Windows;

public sealed class OfflineSandbox(string workspace)
{
    private static readonly string Executable = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsSandbox.exe");

    public static bool CanLaunch => File.Exists(Executable);

    public void Launch()
    {
        if (!CanLaunch)
            throw new LabException("error.sandbox");

        var directory = Path.Combine(workspace, "sandbox");
        LocalPaths.RequireOrdinaryPath(directory);
        Directory.CreateDirectory(directory);
        var profile = Path.Combine(directory, "offline-v1.wsb");
        var expected = CreateConfiguration().ToString(SaveOptions.DisableFormatting);
        if (!File.Exists(profile))
            Export(profile);

        // Reuse the generated profile only if it still matches our settings.
        using var input = new FileStream(LocalPaths.RequireFile(profile, ".wsb"),
            FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 4096)
            throw new LabException("error.sandboxProfile", profile);
        using var reader = new StreamReader(input);
        if (reader.ReadToEnd().Trim() != expected)
            throw new LabException("error.sandboxProfile", profile);

        var launch = new ProcessStartInfo(Executable) { UseShellExecute = false };
        launch.ArgumentList.Add(profile);
        using var process = Process.Start(launch);
    }

    public static void Export(string path) => OutputFile.WriteNew(path, ".wsb", stream =>
    {
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true);
        writer.Write(CreateConfiguration().ToString(SaveOptions.DisableFormatting));
    });

    private static XElement CreateConfiguration() => new("Configuration",
        new XElement("vGPU", "Disable"),
        new XElement("Networking", "Disable"),
        new XElement("ClipboardRedirection", "Disable"),
        new XElement("AudioInput", "Disable"),
        new XElement("VideoInput", "Disable"),
        new XElement("PrinterRedirection", "Disable"),
        new XElement("ProtectedClient", "Enable"));
}
