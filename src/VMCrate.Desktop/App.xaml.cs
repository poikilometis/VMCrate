using System.Windows;
using VMCrate.Core;
using VMCrate.Windows;

namespace VMCrate.Desktop;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
        var root = launch?.Workspace ?? (string.IsNullOrWhiteSpace(overrideRoot) ? registration.Read() : overrideRoot);
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
        MainWindow = new WorkspaceWindow(lab, labels, preferences, launch);
        MainWindow.Show();
    }
}
