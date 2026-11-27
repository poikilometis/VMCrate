using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using VMCrate.Core;
using VMCrate.Windows;
using static VMCrate.Tests.Checks;

namespace VMCrate.Tests;

internal static class EvidenceChecks
{
    internal static async Task TemplateHashes()
    {
        using var temp = new TemporaryDirectory();
        var backend = new FakeHypervisor();
        var lab = new LabService(temp.Root, backend, new FakeInbox());
        var id = Guid.NewGuid();
        var state = temp.File("state.json",
            $$"""{"workspaceId":"{{Guid.NewGuid()}}","version":1,"templates":[{"id":"{{id}}","name":"Older","guest":"Linux","secureBoot":true}],"machines":[]}""");
        temp.File(Path.Combine("templates", id.ToString("N"), "base.vhdx"), "old");
        Expect((await lab.VerifyTemplateAsync(id)).Matches is null, "Legacy records must establish a baseline.");
        using (var migrated = JsonDocument.Parse(File.ReadAllText(state)))
            Expect(migrated.RootElement.GetProperty("version").GetInt32() == LabState.CurrentVersion, "The older catalog did not migrate.");

        await lab.ImportTemplateAsync("Current", GuestFamily.Windows, true, temp.File("base.vhdx", "clean"));
        var template = (await lab.ObserveAsync()).Templates.Single(item => item.Name == "Current");
        var initial = await lab.VerifyTemplateAsync(template.Id);
        Expect(initial.Matches == true && initial.Sha256 == Hash("clean"), "Import did not record the copied disk's hash.");

        var baseDisk = Path.Combine(temp.Root, "templates", template.Id.ToString("N"), "base.vhdx");
        File.SetAttributes(baseDisk, FileAttributes.Normal);
        File.WriteAllText(baseDisk, "changed");
        var changed = await lab.VerifyTemplateAsync(template.Id);
        Expect(changed.Matches == false && changed.Sha256 == Hash("changed"), "A changed disk was accepted.");
        Expect((await lab.ObserveAsync()).Templates.Single(item => item.Id == template.Id).Sha256 == initial.Sha256,
            "Verification replaced the reference digest.");
    }

    internal static async Task Report()
    {
        using var temp = new TemporaryDirectory();
        var backend = new FakeHypervisor();
        var lab = new LabService(temp.Root, backend, new FakeInbox());
        var sample = temp.File(Path.Combine("input", "sample.txt"), "file contents");
        await lab.ImportTemplateAsync("Base", GuestFamily.Linux, true, temp.File("base.vhdx", "clean"));
        var template = (await lab.ObserveAsync()).Templates.Single();
        await lab.CreateCloneAsync(template.Id, new MachineOptions("Report VM", 4, 2));
        var id = (await lab.ObserveAsync()).Machines.Single().Id;
        var result = Path.Combine(temp.Root, "report.json");
        await lab.ExportReportAsync(result, id, [sample, sample]);

        var json = File.ReadAllText(result);
        var report = JsonSerializer.Deserialize<AnalysisReport>(json, JsonDefaults.Options)!;
        Expect(report.Files.Single() == new FileEvidence("sample.txt", 13, Hash("file contents")), "File metadata differs.");
        Expect(report.Machine?.Id == id && report.Machine?.Power == PowerState.Off &&
            report.Machine?.TemplateSha256 == Hash("clean"), "VM audit evidence is missing.");
        Expect(!json.Contains(temp.Root, StringComparison.OrdinalIgnoreCase), "The report includes a host path.");
        await ErrorAsync("error.outputExists", () => lab.ExportReportAsync(result, id, []));
        await ErrorAsync("error.reportEmpty", () => lab.ExportReportAsync(Path.Combine(temp.Root, "empty.json"), null, []));

        backend.FailObserve = true;
        var hostOffline = Path.Combine(temp.Root, "unavailable.json");
        await lab.ExportReportAsync(hostOffline, id, []);
        var unverified = JsonSerializer.Deserialize<AnalysisReport>(File.ReadAllText(hostOffline), JsonDefaults.Options)!;
        Expect(unverified.Machine?.Power == PowerState.Unavailable &&
            unverified.Machine.Findings.SequenceEqual(new[] { "audit.unavailable" }),
            "Report did not distinguish missing configuration observations.");
    }

    internal static void SandboxProfile()
    {
        using var temp = new TemporaryDirectory();
        var profile = Path.Combine(temp.Root, "offline.wsb");
        OfflineSandbox.Export(profile);
        var config = XElement.Load(profile);
        var disabled = new[] { "vGPU", "Networking", "ClipboardRedirection", "AudioInput", "VideoInput", "PrinterRedirection" };
        Expect(config.Name == "Configuration" && disabled.All(name => config.Element(name)?.Value == "Disable") &&
            config.Element("ProtectedClient")?.Value == "Enable" && config.Elements().Count() == disabled.Length + 1,
            "Offline Sandbox profile has unexpected capabilities.");
    }

    private static string Hash(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
