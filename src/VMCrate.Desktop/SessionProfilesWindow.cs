using System.Windows;
using System.Windows.Controls;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class SessionProfilesWindow : WorkflowWindow
{
    private readonly LabService lab;
    private readonly ListBox profiles;
    private readonly Button apply;
    private readonly Button remove;

    internal SessionProfilesWindow(Labels text, LabService lab, string workspace, Guid? templateId, MachineOptions options)
        : base(text, "profiles.title", 570, 470)
    {
        this.lab = lab;
        profiles = Field("profiles.title", new ListBox { DisplayMemberPath = nameof(SessionProfile.Name), MinHeight = 100, MaxHeight = 230 });
        var buttons = Actions();
        apply = Action(buttons, "action.apply", ApplyAsync, primary: true);
        var save = Action(buttons, "profiles.save", async () =>
        {
            if (templateId is Guid id)
            {
                var created = await Task.Run(() => lab.SaveSessionProfile(id, options));
                await RefreshAsync(created.Id);
            }
        });
        save.IsEnabled = templateId is not null && !string.IsNullOrWhiteSpace(options.Name);
        remove = Action(buttons, "profiles.remove", RemoveAsync);
        var shortcut = Action(buttons, "profiles.shortcut", () => SaveShortcutAsync(workspace));
        apply.IsEnabled = remove.IsEnabled = shortcut.IsEnabled = false;
        profiles.SelectionChanged += (_, _) => apply.IsEnabled = remove.IsEnabled = shortcut.IsEnabled = Current is not null;
        Loaded += async (_, _) => await RunAsync(() => RefreshAsync());
    }

    internal SessionProfile? Selected { get; private set; }
    private SessionProfile? Current => profiles.SelectedItem as SessionProfile;

    private async Task RefreshAsync(Guid? select = null)
    {
        var available = await Task.Run(lab.ReadSessionProfiles);
        profiles.ItemsSource = available;
        profiles.SelectedItem = available.FirstOrDefault(item => item.Id == select) ?? available.FirstOrDefault();
    }

    private Task ApplyAsync()
    {
        Selected = Current;
        if (Selected is not null)
            FinishDialog();
        return Task.CompletedTask;
    }

    private Task SaveShortcutAsync(string workspace)
    {
        if (Current is not { } profile || SaveFile("profiles.shortcut", "VMCrate-session.lnk", ".lnk") is not { } path)
            return Task.CompletedTask;
        SessionShortcut.Save(path, profile, workspace);
        return Task.CompletedTask;
    }

    private async Task RemoveAsync()
    {
        if (Current is { } profile && Text.Confirm(this, "profiles.confirmRemove", profile.Name))
        {
            await Task.Run(() => lab.DeleteSessionProfile(profile.Id));
            await RefreshAsync();
        }
    }
}
