using System.Windows;
using System.Windows.Controls;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class StorageWindow : WorkflowWindow
{
    private readonly LabService lab;
    private readonly TextBlock summary;
    private readonly ListBox templates, machines, images;
    private readonly Button deleteMedia, deleteTemplate;

    internal StorageWindow(Labels text, LabService lab) : base(text, "tools.storage", 830)
    {
        this.lab = lab;
        summary = Note("");
        Action(Actions(), "action.refresh", RefreshAsync, true);

        var tabs = new TabControl { Height = 318, Margin = new Thickness(0, 0, 0, 8) };
        templates = AddTab(tabs, "tab.templates");
        machines = AddTab(tabs, "tab.machines");
        images = AddTab(tabs, "label.media");
        Body.Children.Add(tabs);
        var operations = Actions();
        deleteTemplate = Action(operations, "action.removeTemplate", DeleteTemplateAsync);
        deleteMedia = Action(operations, "action.removeMedia", DeleteMediaAsync);
        templates.SelectionChanged += (_, _) => Buttons(tabs);
        images.SelectionChanged += (_, _) => Buttons(tabs);
        tabs.SelectionChanged += (_, _) => Buttons(tabs);
        Buttons(tabs);
        Loaded += async (_, _) => await RunAsync(RefreshAsync);
    }

    private ListBox AddTab(TabControl tabs, string label)
    {
        var list = new ListBox { DisplayMemberPath = "Text" };
        tabs.Items.Add(new TabItem { Header = Text[label], Content = list });
        return list;
    }

    private void Buttons(TabControl tabs)
    {
        deleteTemplate.IsEnabled = tabs.SelectedIndex == 0 && templates.SelectedItem is StorageRow { Value: StorageTemplate { Dependents.Count: 0 } };
        deleteMedia.IsEnabled = tabs.SelectedIndex == 2 && images.SelectedItem is StorageRow;
    }

    private async Task RefreshAsync()
    {
        var data = await Task.Run(lab.InspectStorage);
        summary.Text = data.Directory + Environment.NewLine +
            Text.Format("storage.free", data.FreeBytes is long free ? MiB(free) : "—") + Environment.NewLine +
            Text.Format("storage.summary", MiB(data.TemplateBytes), MiB(data.MachineBytes), MiB(data.MediaBytes)) +
            Environment.NewLine + Text["points.title"] + $" · {MiB(data.RecoveryPointBytes)} MiB";
        templates.ItemsSource = data.Templates.Select(item => new StorageRow(item,
            $"{item.Name} · {item.Revision ?? "—"} · {MiB(item.Bytes)} MiB" +
            (item.Dependents.Count == 0 ? "" : Environment.NewLine + Text.Format("storage.dependencies", string.Join(", ", item.Dependents))))).ToArray();
        machines.ItemsSource = data.Machines.Select(vm => new StorageRow(vm,
            $"{vm.Name} · {MiB(vm.DiskBytes + vm.InboxBytes)} MiB")).ToArray();
        images.ItemsSource = data.Media.Select(item => new StorageRow(item,
            $"{item.Sha256} · {MiB(item.Bytes)} MiB · {Text["tools.builds"]} {item.Builds}")).ToArray();
    }

    private async Task DeleteTemplateAsync()
    {
        if (templates.SelectedItem is StorageRow { Value: StorageTemplate item } &&
            Text.Confirm(this, "confirm.removeTemplate", item.Name))
        {
            await Task.Run(() => lab.DeleteTemplateAsync(item.Id));
            await RefreshAsync();
        }
    }

    private async Task DeleteMediaAsync()
    {
        if (images.SelectedItem is StorageRow { Value: StorageMedia item } &&
            Text.Confirm(this, "confirm.removeMedia", item.Sha256))
        {
            await Task.Run(() => lab.DeleteUnusedInstallerAsync(item.Sha256));
            await RefreshAsync();
        }
    }

    private sealed record StorageRow(object Value, string Text);
}
