using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using VMCrate.Core;
using VMCrate.Host;
using static VMCrate.Tests.Checks;

namespace VMCrate.Tests;

internal static class RemoteQueueChecks
{
    internal static async Task AccountIsolationAndRestart()
    {
        using var fixture = new TemporaryDirectory();
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
            { new(CertificatePins.ClientAuthentication) }, critical: true));
        using var aliceCertificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var bobCertificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var blueprint = new LabBlueprint(1, "team-offline", [new("Station", "base", 4, 2)]);
        var host = new FakeHypervisor();
        var directory = Path.Combine(fixture.Root, "queue");
        var recipe = new LabRecipe("offline", "Offline", "Fixture", "MIT", 1, "0.6.0", ["https://www.debian.org/"], blueprint);
        var catalog = fixture.File("recipes.json", JsonSerializer.Serialize(new[] { recipe }, JsonDefaults.Options));
        var alice = await AccountAsync("alice", aliceCertificate);
        var bob = await AccountAsync("bob", bobCertificate);
        var definition = new HostSettings(1, "127.0.0.1", 7443, new string('0', 64), directory, catalog, [alice, bob]);
        var path = fixture.File("host.json", JsonSerializer.Serialize(definition, JsonDefaults.Options));
        var settings = QueueSettings.Read(path);
        Expect(settings.ForCertificate(aliceCertificate)?.Id == "alice" && settings.ForCertificate(bobCertificate)?.Id == "bob" &&
            settings.ForCertificate(null) is null, "The same display name must not identify two client certificates.");
        var owner = settings.Accounts[0];
        var other = settings.Accounts[1];
        var submitted = new RemoteRequest(Guid.NewGuid(), recipe.Id, RemoteLabAction.Deploy);
        using (var ledger = new JobLedger(directory))
        {
            var first = ledger.Submit(owner, submitted);
            Expect(first == ledger.Submit(owner, submitted) && ledger.List(owner).Length == 1 &&
                ledger.List(other).Length == 0, "A replay must be idempotent and isolated to the authenticated owner.");
            await ErrorAsync("error.remoteJob", () => Task.FromResult(ledger.Retry(other, first.Id)));
            await ErrorAsync("error.remoteJob", () => Task.FromResult(ledger.Submit(owner, submitted with { Action = RemoteLabAction.Start })));
            Expect(ledger.Claim()?.Job.Id == first.Id, "Worker must claim the first durable job.");
        }

        using (var ledger = new JobLedger(directory))
        {
            Expect(ledger.List(owner).Single().Status == RemoteJobStatus.Interrupted && ledger.Claim() is null,
                "An interrupted job must wait for an explicit retry.");
            var retry = ledger.Retry(owner, submitted.Id);
            Expect(retry.Id == submitted.Id && retry.Status == RemoteJobStatus.Queued, "Retry changed the request identity.");
            var claimed = ledger.Claim()!;
            var approved = owner.Recipes[claimed.Job.Recipe];
            var lab = new LabService(owner.Workspace, host, new FakeInbox());
            await lab.RunPinnedLabAsync(approved.Blueprint, approved.Pins, claimed.Job.Action);
            ledger.Complete(claimed);
            Expect(ledger.List(owner).Single().Status == RemoteJobStatus.Succeeded &&
                (await lab.ObserveAsync()).Machines is [{ Network: NetworkMode.Offline }] &&
                host.Machines.Count == 1, "The approved recipe must produce only its scoped Offline clone.");
        }

        var collision = definition with { Accounts = [alice, bob with { Workspace = Path.Combine(alice.Workspace, "nested") }] };
        Directory.CreateDirectory(collision.Accounts[1].Workspace);
        File.Copy(Path.Combine(bob.Workspace, "state.json"), Path.Combine(collision.Accounts[1].Workspace, "state.json"));
        File.WriteAllText(path, JsonSerializer.Serialize(collision, JsonDefaults.Options));
        await ErrorAsync("error.remoteConfig", () => Task.FromResult(QueueSettings.Read(path)));

        async Task<AccountSettings> AccountAsync(string id, X509Certificate2 certificate)
        {
            var workspace = Path.Combine(fixture.Root, id);
            var lab = new LabService(workspace, host, new FakeInbox());
            await lab.ImportTemplateAsync("base", GuestFamily.Linux, false, fixture.File(id + ".vhdx", id));
            var pins = Path.Combine(fixture.Root, id + ".lock.json");
            await OfflinePlanning.SaveLockAsync(pins, workspace, blueprint);
            return new(id, certificate.GetCertHashString(HashAlgorithmName.SHA256), workspace, [new(recipe.Id, pins)]);
        }
    }

    internal static async Task PinnedExecution()
    {
        using var fixture = new TemporaryDirectory();
        var host = new FakeHypervisor();
        var root = Path.Combine(fixture.Root, "workspace");
        var lab = new LabService(root, host, new FakeInbox());
        await lab.ImportTemplateAsync("base", GuestFamily.Linux, false, fixture.File("base.vhdx", "trusted data"));
        var blueprint = new LabBlueprint(1, "approved", [new("Station", "base", 4, 2)]);
        var lockFile = Path.Combine(fixture.Root, "recipe.lock.json");
        await OfflinePlanning.SaveLockAsync(lockFile, root, blueprint);
        var pins = OfflinePlanning.ReadLock(lockFile);
        var disk = Directory.EnumerateFiles(root, "base.vhdx", SearchOption.AllDirectories).Single();
        File.SetAttributes(disk, FileAttributes.Normal);
        await File.WriteAllTextAsync(disk, "changed data");
        await ErrorAsync("error.projectIntegrity", () => lab.RunPinnedLabAsync(blueprint, pins, RemoteLabAction.Deploy));
        Expect(host.Machines.Count == 0, "No host mutation may occur with a different disk digest.");
        await File.WriteAllTextAsync(disk, "trusted data");
        await lab.RunPinnedLabAsync(blueprint, pins, RemoteLabAction.Deploy);
        await lab.RunPinnedLabAsync(blueprint, pins, RemoteLabAction.Start);
        Expect(host.Machines.Values.Single().Power == PowerState.Running, "The selected lab did not start.");
        await File.WriteAllTextAsync(disk, "changed data");
        await lab.RunPinnedLabAsync(blueprint, pins, RemoteLabAction.PowerOff);
        Expect(host.Machines.Values.Single().Power == PowerState.Off, "An approved stop must still work after disk drift.");
        await ErrorAsync("error.projectIntegrity", () => lab.RunPinnedLabAsync(blueprint, pins, RemoteLabAction.Start));
    }
}
