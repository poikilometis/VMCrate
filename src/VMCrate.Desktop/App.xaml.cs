using System.Windows;
using VMCrate.Core;
using VMCrate.Windows;

namespace VMCrate.Desktop;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args is ["--release-smoke"])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var smokePreferences = new Preferences(new WorkspaceRegistration().DefaultWorkspace);
                var smokeLabels = new Labels(new TextCatalog("en"));
                var surface = new WorkspaceWindow(new LabService(smokePreferences.Root, new PowerShellHypervisor(), new UdfInboxWriter()),
                    smokeLabels, smokePreferences);
                surface.Measure(new Size(1080, 740));
                surface.Arrange(new Rect(0, 0, 1080, 740));
                Shutdown(0);
            }
            catch (Exception) { Shutdown(1); }
            return;
        }
        if (e.Args is ["--local-jobs", var workspace])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await LocalJobProcess.RunAsync(workspace); Shutdown(0); }
            catch (Exception) { Shutdown(1); }
            return;
        }
        if (ProxyLeaseWatcher.Parse(e.Args) is { } watcher)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { Shutdown(await watcher.RunAsync()); }
            catch (Exception) { Shutdown(1); }
            return;
        }
        var launch = SessionLaunch.Parse(e.Args);
        var overrideRoot = Environment.GetEnvironmentVariable("VMCRATE_DATA_DIR");
        var registration = new WorkspaceRegistration();
        var root = launch?.Workspace ?? (string.IsNullOrWhiteSpace(overrideRoot)
            ? ReleaseUpdate.InstalledWorkspace() ?? registration.Read() : overrideRoot);
        var newRegistration = root is null;
        var hypervisor = new PowerShellHypervisor();
        if (root is null)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var initial = new Preferences(registration.DefaultWorkspace);
            var setupText = new Labels(new TextCatalog(initial.ReadLanguage()));
            var setup = new FirstRunWindow(setupText, initial.Root, hypervisor.ReadHostReadinessAsync,
                directory => root = registration.Save(directory));
            if (setup.ShowDialog() != true)
            {
                Shutdown();
                return;
            }
            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }
        var preferences = new Preferences(root);
        var labels = new Labels(new TextCatalog(preferences.ReadLanguage()));
        var lab = new LabService(preferences.Root, hypervisor, new UdfInboxWriter());
        ReleaseUpdate? update;
        try { update = ReleaseUpdate.Current(preferences.Root); }
        catch (Exception error)
        {
            labels.ShowError(null, error);
            Shutdown(1);
            return;
        }
        if (update is { } candidate && (e.Args is ["--workspace-update", _] ||
            !System.IO.File.Exists(System.IO.Path.Combine(WorkspaceSchemaUpdate.DirectoryFor(preferences.Root, candidate.Id), "schema.json"))))
        {
            try
            {
                if (e.Args is ["--workspace-update", var ticket] && ticket != candidate.Id.ToString("N") ||
                    !string.Equals(candidate.Executable(preferences.Root), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                    throw new LabException("error.appUpdate");
                await Task.Run(() => WorkspaceSchemaUpdate.Prepare(preferences.Root, candidate.Id));
            }
            catch (Exception error)
            {
                labels.ShowError(this.MainWindow, error);
                Shutdown(1);
                return;
            }
        }
        MainWindow = new WorkspaceWindow(lab, labels, preferences, launch, showFirstMachine: newRegistration, appUpdate: update);
        MainWindow.Show();
        try
        {
            await Task.Run(() => ProxyLeaseWatcher.WakeAsync(lab));
            await Task.Run(() => LocalJobProcess.WakePending(preferences.Root));
        }
        catch (Exception error) { labels.ShowError(MainWindow, error); }
    }
}
