using System.IO;
using System.Runtime.InteropServices;
using VMCrate.Core;
using VMCrate.Desktop;
using static VMCrate.Tests.Checks;

namespace VMCrate.Tests;

internal static class SessionProfileChecks
{
    internal static async Task ProfileAndShortcut()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Root, "workspace with spaces");
        var lab = new LabService(root, new FakeHypervisor(), new FakeInbox());
        await lab.ImportTemplateAsync("Fixture", GuestFamily.Windows, true, temp.File("base.vhdx", "test disk"));
        var template = (await lab.ObserveAsync()).Templates.Single();
        var profile = lab.SaveSessionProfile(template.Id, new("Clean inspection", 4, 2));
        await ErrorAsync("error.duplicateName", Run(() => lab.SaveSessionProfile(template.Id, new(profile.Name, 6, 3))));
        var reopened = new LabService(root, new FakeHypervisor(), new FakeInbox());
        Expect(reopened.ReadSessionProfiles().Single() == profile, "The profile did not survive reopening.");
        Expect((await reopened.ObserveAsync()).Machines.Count == 0, "A profile created a VM before confirmation.");

        var shortcutPath = Path.Combine(temp.Root, "session.lnk");
        SessionShortcut.Save(shortcutPath, profile, root);
        await ErrorAsync("error.outputExists", Run(() => SessionShortcut.Save(shortcutPath, profile, root)));
        CheckShortcut(shortcutPath, profile, root);
        var file = temp.File("example.txt", "test input");
        var request = SessionLaunch.Parse(["--session-profile", profile.Id.ToString("N"), root, file]);
        Expect(request?.ProfileId == profile.Id && request.Workspace == root && request.Files.SequenceEqual([file]),
            "The shortcut launch lost its workspace or selected files.");
        Expect(SessionLaunch.Parse(["--session-profile", profile.Id.ToString("N"), "relative"]) is null,
            "Startup accepted a relative workspace from a shortcut.");
        reopened.DeleteSessionProfile(profile.Id);
        Expect(reopened.ReadSessionProfiles().Count == 0 && (await reopened.ObserveAsync()).Templates.Single() == template,
            "Removing a profile modified its template.");
    }

    private static void CheckShortcut(string shortcutPath, SessionProfile profile, string workspace)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell")!;
        object shellObject = Activator.CreateInstance(type)!;
        object? shortcut = null;
        try
        {
            dynamic shell = shellObject;
            shortcut = shell.CreateShortcut(shortcutPath);
            dynamic link = shortcut;
            Expect((string)link.TargetPath == Path.Combine(AppContext.BaseDirectory, "VMCrate.exe") &&
                (string)link.Arguments == $"--session-profile {profile.Id:N} \"{workspace}\"",
                "The native shortcut does not target VMCrate and the saved profile.");
        }
        finally
        {
            if (shortcut is not null)
                Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shellObject);
        }
    }
}
