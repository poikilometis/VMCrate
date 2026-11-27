using System.Windows;
using VMCrate.Core;

namespace VMCrate.Desktop;

public partial class WorkspaceWindow
{
    private async void OnRecipes(object sender, RoutedEventArgs e) =>
        await ShowWorkflowAsync(new RecipeWindow(labels, lab, snapshot?.Templates ?? []));

    private async void OnOutbox(object sender, RoutedEventArgs e) =>
        await ShowWorkflowAsync(new OutboxWindow(labels, lab));

    private void OnAppUpdate(object sender, RoutedEventArgs e)
    {
        var form = new AppUpdateWindow(labels, lab.WorkspaceDirectory, appUpdate) { Owner = this };
        if (form.ShowDialog() != true || form.Selection is not { } result)
            return;
        try
        {
            var executable = result.Rollback ? result.Update.PreviousExecutable : result.Update.Executable(lab.WorkspaceDirectory);
            ReleaseUpdate.Start(executable, lab.WorkspaceDirectory, result.Rollback ? null : result.Update.Id);
            Application.Current.Shutdown();
        }
        catch (Exception error) { labels.ShowError(this, error is LabException ? error : new LabException("error.appUpdate", inner: error)); }
    }

    private async void OnRemoteQueue(object sender, RoutedEventArgs e) =>
        await ShowWorkflowAsync(new RemoteQueueWindow(labels));

    private async void OnMachineTransfer(object sender, RoutedEventArgs e) =>
        await ShowWorkflowAsync(new MachineTransferWindow(labels, lab, snapshot));

    private async void OnImages(object sender, RoutedEventArgs e)
    {
        var catalog = new ImageCatalogWindow(labels, lab);
        await ShowWorkflowAsync(catalog);
        if (catalog.Selection is not { } choice)
            return;

        var build = new NewTemplateBuildWindow(labels, lab, choice);
        await ShowWorkflowAsync(build);
        if (build.Created)
            await ShowWorkflowAsync(new TemplateBuildWindow(labels, lab));
    }
}
