using System.Windows;

namespace VMCrate.Desktop;

public partial class WorkspaceWindow
{
    private async void OnRecipes(object sender, RoutedEventArgs e) =>
        await ShowWorkflowAsync(new RecipeWindow(labels, lab, snapshot?.Templates ?? []));

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
