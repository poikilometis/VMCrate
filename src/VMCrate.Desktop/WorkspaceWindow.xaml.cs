using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VMCrate.Core;

namespace VMCrate.Desktop;

public partial class WorkspaceWindow : Window
{
    private readonly LabService lab;
    private readonly Labels labels;
    private readonly Preferences preferences;
    private readonly SessionLaunch? launch;
    private readonly DispatcherTimer sessions = new() { Interval = TimeSpan.FromSeconds(15) };
    private LabSnapshot? snapshot;
    private bool busy;
    private bool workflowOpen;
    private string? status;
    private LabException? catalogIssue;
    private Machine? SelectedMachine => (MachineList.SelectedItem as LabRow)?.Item as Machine;
    private VMCrate.Core.Template? SelectedTemplate => (TemplateList.SelectedItem as LabRow)?.Item as VMCrate.Core.Template;

    internal WorkspaceWindow(LabService lab, Labels labels, Preferences preferences, SessionLaunch? launch = null)
    {
        this.lab = lab;
        this.labels = labels;
        this.preferences = preferences;
        this.launch = launch;
        InitializeComponent();
        DataContext = labels;
        labels.Apply(this);
        LanguagePicker.ItemsSource = TextCatalog.Locales;
        LanguagePicker.SelectedValue = labels.Current.Id;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += (_, _) => sessions.Stop();
        sessions.Tick += OnSessionTick;
        Render();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await RunAsync();
        sessions.Start();
        if (launch is null)
            return;

        try
        {
            var profiles = await Task.Run(lab.ReadSessionProfiles);
            var profile = profiles.SingleOrDefault(item => item.Id == launch.ProfileId)
                ?? throw new LabException("error.profile");
            await ShowWorkflowAsync(new CleanSessionWindow(labels, lab, preferences.Root, profile, launch.Files));
        }
        catch (Exception error)
        {
            labels.ShowError(this, error);
        }
    }

    private async void OnSessionTick(object? sender, EventArgs e)
    {
        if (!workflowOpen && snapshot?.Machines.Any(machine => machine.Disposable && machine.SessionStarted) == true)
            await RunAsync(automatic: true);
    }

    private void OnClosing(object? sender, CancelEventArgs e) => e.Cancel = busy;
    private async void OnRefresh(object sender, RoutedEventArgs e) => await RunAsync();

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguagePicker.SelectedValue is not string code || labels.Current.Id == code)
            return;

        labels.Change(code);
        labels.Apply(this);
        Render();
        try
        {
            preferences.WriteLanguage(code);
        }
        catch (Exception error)
        {
            labels.ShowError(this, error);
        }
    }

    private void OnHostDetails(object sender, RoutedEventArgs e)
    {
        if ((catalogIssue ?? snapshot?.HostError ?? snapshot?.CleanupError) is { } error)
            labels.ShowError(this, error);
    }

    private void OnMachineChanged(object sender, SelectionChangedEventArgs e) => RenderMachine();
    private void OnTemplateChanged(object sender, SelectionChangedEventArgs e) => RenderTemplate();

    private async Task RunAsync(Func<Task>? action = null, bool automatic = false, string successStatus = "status.done")
    {
        if (busy)
            return;

        busy = true;
        if (!automatic)
            SetStatus("status.working");
        SetBusyControls();
        try
        {
            if (action is not null)
                await Task.Run(action);
            if (!automatic)
                status = action is null ? null : successStatus;
        }
        catch (Exception error)
        {
            status = "status.error";
            labels.ShowError(this, error);
        }

        try
        {
            snapshot = await Task.Run(lab.RefreshAsync);
            catalogIssue = null;
        }
        catch (LabException error) when (error.Key is "error.state" or "error.stateVersion")
        {
            snapshot = null;
            catalogIssue = error;
            status = error.Key;
        }
        catch (Exception error)
        {
            snapshot = null;
            catalogIssue = null;
            status = "status.error";
            if (!automatic)
                labels.ShowError(this, error);
        }
        finally
        {
            busy = false;
            Render();
        }
    }

    private void SetBusyControls()
    {
        WorkspaceTabs.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        LanguagePicker.IsEnabled = !busy;
        WorkingBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetStatus(string? key)
    {
        status = key;
        OperationStatus.Text = key is null ? string.Empty : labels[key];
    }

    private sealed record LabRow(Guid Id, string Name, string Summary, object Item);
    private sealed record NetworkChoice(NetworkMode Mode, string Name);
}
