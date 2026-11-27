using System.Globalization;

namespace VMCrate.Core;

public sealed partial class LabService
{
    public async Task CreateInstallerAsync(InstallerOptions options)
    {
        var iso = LocalPaths.RequireFile(options.IsoPath, ".iso");
        if (options.DiskGiB is < 16 or > 2048)
            throw new LabException("error.diskSize");

        using var workspaceLock = store.Lock();
        var state = store.Load();
        var machine = NewMachine(state, options.Machine, options.Guest, options.SecureBoot, templateId: null);
        await ProvisionMachineAsync(state, machine, iso, options.DiskGiB);
    }

    public async Task<Machine> CreateCloneAsync(Guid templateId, MachineOptions options, bool disposable = false)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var template = FindTemplate(state, templateId);
        RequirePublishedTemplate(template);
        LocalPaths.RequireFile(store.TemplateDisk(template.Id), ".vhdx");
        var machine = NewMachine(state, options, template.Guest, template.SecureBoot, template.Id)
            with { Disposable = disposable };
        await ProvisionMachineAsync(state, machine, null, 0);
        return machine;
    }

    public async Task ImportTemplateAsync(string name, GuestFamily guest, bool secureBoot, string source)
    {
        var disk = LocalPaths.RequireFile(source, ".vhdx");
        using var workspaceLock = store.Lock();
        var state = store.Load();
        await StoreTemplateAsync(state, name, guest, secureBoot, disk);
    }

    public async Task CaptureTemplateAsync(Guid id, string name)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var machine = FindMachine(state, id);
        if (machine.TemplateId is not null)
            throw new LabException("error.builderOnly");

        var source = await hypervisor.GetCaptureDiskAsync(store.Location(state, machine));
        await StoreTemplateAsync(state, name, machine.Guest, machine.SecureBoot,
            LocalPaths.RequireFile(source, ".vhdx"));
    }

    public async Task DeleteTemplateAsync(Guid id)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var template = FindTemplate(state, id);
        if (state.Machines.Any(machine => machine.TemplateId == id) ||
            state.Builds.Any(job => job.Options.RevisionTrial && job.Options.PreviousTemplateId == id && job.Stage != BuildStage.Complete) ||
            (!template.Published && state.Builds.Any(job => job.Id == id)))
            throw new LabException("error.inUse");

        LocalPaths.RemoveDirectory(store.Templates, id);
        state.Templates.Remove(template);
        store.Save(state);
        await Task.CompletedTask;
    }

    private static void RequirePublishedTemplate(Template template)
    {
        if (!template.Published)
            throw new LabException("error.buildBoot", template.Name);
    }

    private static Machine NewMachine(LabState state, MachineOptions options, GuestFamily guest,
        bool secureBoot, Guid? templateId)
    {
        var name = ValidateName(options.Name, state.Machines.Select(machine => machine.Name));
        ValidateResources(options.MemoryGiB, options.CpuCount);
        return new Machine(Guid.NewGuid(), name, guest, secureBoot, templateId,
            options.MemoryGiB, options.CpuCount, NetworkMode.Offline);
    }

    private static void ValidateResources(int memoryGiB, int cpuCount)
    {
        if (memoryGiB is < 2 or > 128 || cpuCount is < 1 or > 32)
            throw new LabException("error.resources");
    }

    private static string ValidateName(string input, IEnumerable<string> existing)
    {
        var name = input.Trim();
        if (name.Length is < 1 or > 64 || name.Any(c => char.IsControl(c) ||
            char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator))
            throw new LabException("error.name");

        if (existing.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new LabException("error.duplicateName");
        return name;
    }
}
