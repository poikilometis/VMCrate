using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class FirstRunWindow : WorkflowWindow
{
    private readonly Func<Task<HostReadiness>> checkHost;
    private readonly Action<string>? select;
    private readonly TextBox directory;
    private readonly TextBlock volume, readiness;

    internal FirstRunWindow(Labels text, string path, Func<Task<HostReadiness>> checkHost, Action<string>? select)
        : base(text, "tools.firstRun", 640, 545)
    {
        this.checkHost = checkHost;
        this.select = select;
        if (select is not null)
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Note(Text["hint.firstRun"]);
        directory = Field("setup.workspace", new TextBox { Text = path, IsReadOnly = true, FlowDirection = FlowDirection.LeftToRight });
        if (select is not null)
            Action(Actions(), "action.browse", BrowseAsync);
        volume = Note("");
        readiness = Note("");
        var buttons = Actions();
        Action(buttons, "action.refresh", CheckAsync);
        if (select is not null)
            Action(buttons, "setup.open", SaveAsync, true);
        Loaded += async (_, _) => await RunAsync(CheckAsync);
    }

    private Task BrowseAsync()
    {
        var picker = new OpenFolderDialog { Title = Text["setup.workspace"], Multiselect = false };
        if (picker.ShowDialog(this) == true)
        {
            directory.Text = picker.FolderName;
            ShowVolume();
        }
        return Task.CompletedTask;
    }

    private async Task CheckAsync()
    {
        ShowVolume();
        var result = await Task.Run(checkHost);
        var lines = new List<string>
        {
            Text[result.Windows11X64 ? "setup.windowsReady" : "setup.windowsRequired"],
            Text["setup." + result.HyperV],
            Text[result.SandboxAvailable ? "setup.sandboxReady" : "setup.sandboxOptional"]
        };
        if (result.Capacity is { } capacity)
        {
            lines.Add($"Windows · {capacity.Edition ?? "—"} · {capacity.WindowsBuild}");
            lines.Add(Text.Format("setup.capacity", capacity.LogicalProcessors?.ToString() ?? "—",
                capacity.FreeMemoryBytes is long free ? MiB(free) : "—"));
            if (capacity.FirmwareVirtualization == false)
                lines.Add(Text["setup.firmwareOff"]);
            if (capacity.RestartRequired == true)
                lines.Add(Text["setup.restartPending"]);
        }
        if (!string.IsNullOrWhiteSpace(result.Detail))
            lines.Add(result.Detail);
        readiness.Text = string.Join(Environment.NewLine, lines);
    }

    private void ShowVolume()
    {
        var status = WorkspaceVolume.Inspect(directory.Text);
        volume.Text = status.FreeBytes is long bytes
            ? Text.Format("setup.free", MiB(bytes / 1024), status.FileSystem ?? "")
            : Text["setup.freeUnknown"];
        if (status.ExistingCatalog)
            volume.Text += Environment.NewLine + Text["setup.existing"];
    }

    private async Task SaveAsync()
    {
        var path = directory.Text;
        await Task.Run(() => select!(path));
        FinishDialog();
    }
}
