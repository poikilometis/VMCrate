using System.Windows.Controls;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class FirstMachineWindow : WorkflowWindow
{
    private readonly LabService lab;
    private readonly string root;
    private readonly Func<Task<HostReadiness>> checkHost;
    private readonly TextBlock status;
    private readonly ComboBox templates;
    private readonly Button launch;
    private LabSnapshot? snapshot;
    private Guid? machine;
    private bool ready;

    internal FirstMachineWindow(Labels text, LabService lab, string root, Func<Task<HostReadiness>> checkHost)
        : base(text, "first.title")
    {
        this.lab = lab;
        this.root = root;
        this.checkHost = checkHost;
        status = Note("");
        var host = Actions();
        Action(host, "action.refresh", RefreshAsync);
        Action(host, "tools.recovery", async () =>
        {
            new RecoveryWindow(Text, lab) { Owner = this }.ShowDialog();
            await RefreshAsync();
        });
        templates = Field("first.template", new ComboBox { DisplayMemberPath = nameof(VMCrate.Core.Template.Name) });
        var sources = Actions();
        Action(sources, "action.importTemplate", ImportAsync);
        Action(sources, "tools.images", ChooseImageAsync);
        Action(sources, "tools.builds", BuildAsync);
        launch = Action(Actions(), "first.launch", LaunchAsync, primary: true);
        launch.IsEnabled = false;
        templates.SelectionChanged += (_, _) => RenderLaunch();
        Loaded += async (_, _) => await RunAsync(RefreshAsync);
    }

    private async Task RefreshAsync()
    {
        var selected = (templates.SelectedItem as Template)?.Id;
        var host = await Task.Run(checkHost);
        snapshot = await Task.Run(lab.ObserveAsync);
        ready = host.Windows11X64 && host.HyperV == HyperVReadiness.Ready && snapshot.HostError is null;
        var volume = WorkspaceVolume.Inspect(root);
        var free = volume.FreeBytes is long bytes ? Text.Format("setup.free", MiB(bytes / 1024), volume.FileSystem ?? "")
            : Text["setup.freeUnknown"];
        var step = !host.Windows11X64 ? Text["setup.windowsRequired"] : !ready ? Text["setup." + host.HyperV]
            : snapshot.Templates.Count == 0 ? Text["first.needTemplate"] : Text["first.chooseTemplate"];
        status.Text = step + Environment.NewLine + free;
        if (snapshot.HostError is { } error)
            status.Text += Environment.NewLine + Text[error.Key];
        templates.ItemsSource = snapshot.Templates;
        templates.SelectedItem = snapshot.Templates.FirstOrDefault(item => item.Id == selected) ?? snapshot.Templates.FirstOrDefault();
        RenderLaunch();
    }

    private void RenderLaunch()
    {
        launch.IsEnabled = ready && (machine is not null || templates.SelectedItem is Template);
        launch.Content = Text[machine is not null ? "first.continue" : "first.launch"];
    }

    private async Task ImportAsync()
    {
        var dialog = new SetupDialog(Text, SetupKind.Import) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        var (name, guest, secure, disk) = (dialog.Options.Name, dialog.Guest, dialog.SecureBoot, dialog.MediaPath);
        await Task.Run(() => lab.ImportTemplateAsync(name, guest, secure, disk));
        await RefreshAsync();
        templates.SelectedItem = snapshot?.Templates.SingleOrDefault(item => item.Name == name.Trim()) ?? templates.SelectedItem;
    }

    private async Task ChooseImageAsync()
    {
        var catalog = new ImageCatalogWindow(Text, lab) { Owner = this };
        catalog.ShowDialog();
        if (catalog.Selection is not { } choice)
            return;
        var form = new NewTemplateBuildWindow(Text, lab, choice) { Owner = this };
        form.ShowDialog();
        if (form.Created)
            await BuildAsync();
    }

    private async Task BuildAsync()
    {
        new TemplateBuildWindow(Text, lab) { Owner = this }.ShowDialog();
        await RefreshAsync();
    }

    private async Task LaunchAsync()
    {
        if (!ready)
            return;
        if (machine is null)
        {
            if (templates.SelectedItem is not Template template)
                return;
            var form = new SetupDialog(Text, SetupKind.Clone) { Owner = this };
            if (form.ShowDialog() != true)
                return;
            var options = form.Options;
            machine = (await Task.Run(() => lab.CreateCloneAsync(template.Id, options))).Id;
            RenderLaunch();
        }
        var id = machine.Value;
        snapshot = await Task.Run(lab.ObserveAsync);
        if (snapshot.StateOf(id) != PowerState.Running)
            await Task.Run(() => lab.StartAsync(id));
        if (snapshot.Host?.EnhancedSession == true && !Text.Confirm(this, "confirm.console"))
            return;
        await Task.Run(() => lab.OpenConsoleAsync(id));
        FinishDialog();
    }
}
