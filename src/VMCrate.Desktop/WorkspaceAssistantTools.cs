using System.Windows;

namespace VMCrate.Desktop;

public partial class WorkspaceWindow
{
    private async void OnAssistantSettings(object sender, RoutedEventArgs e) =>
        await ShowWorkflowAsync(new AssistantSettingsWindow(labels));

    private async void OnDescriptionBlueprint(object sender, RoutedEventArgs e) =>
        await ShowWorkflowAsync(new DescriptionBlueprintWindow(labels, lab, snapshot?.Templates ?? []));

    private async void OnSessionReports(object sender, RoutedEventArgs e) =>
        await ShowWorkflowAsync(new SessionReportWindow(labels, lab));

    private async void OnAdvisor(object sender, RoutedEventArgs e) =>
        await ShowWorkflowAsync(new UpdateAdvisorWindow(labels, lab, snapshot?.Templates ?? []));

    private async void OnProjects(object sender, RoutedEventArgs e) =>
        await ShowWorkflowAsync(new ProjectWindow(labels, lab));

    private async void OnOperationJournal(object sender, RoutedEventArgs e) =>
        await ShowWorkflowAsync(new OperationJournalWindow(labels, lab));

    private async void OnTemplateRevisions(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate is { } item)
            await ShowWorkflowAsync(new TemplateRevisionsWindow(labels, lab, item));
    }
}
