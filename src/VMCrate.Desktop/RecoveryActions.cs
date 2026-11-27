using System.Windows;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal static class RecoveryActions
{
    internal static string Label(RecoveryRoute route) => route switch
    {
        RecoveryRoute.Host => "tools.diagnostics",
        RecoveryRoute.Workspace => "tools.recovery",
        RecoveryRoute.Resources => "resources.title",
        RecoveryRoute.Proxy => "tools.updates",
        RecoveryRoute.Jobs => "jobs.title",
        RecoveryRoute.Backup => "backup.title",
        RecoveryRoute.Images => "tools.images",
        RecoveryRoute.Revisions => "tools.revisions",
        _ => "tools.help"
    };

    internal static Window Create(RecoveryRoute route, Labels text, LabService lab, LabSnapshot? snapshot = null, Guid? id = null) =>
        route switch
        {
            RecoveryRoute.Host => new HostDiagnosticsWindow(text, lab),
            RecoveryRoute.Workspace => new RecoveryWindow(text, lab),
            RecoveryRoute.Resources => new ResourcePlanWindow(text, lab),
            RecoveryRoute.Proxy when snapshot?.Machines.FirstOrDefault(vm => vm.Id == id) is { TemplateId: null } target
                => new UpdateProxyWindow(text, lab, target),
            RecoveryRoute.Proxy => new HostDiagnosticsWindow(text, lab),
            RecoveryRoute.Jobs => new LocalJobsWindow(text, lab),
            RecoveryRoute.Backup => new LabBackupWindow(text, lab, snapshot?.Machines ?? []),
            RecoveryRoute.Images => new ImageCatalogWindow(text, lab),
            RecoveryRoute.Revisions when snapshot?.Templates.FirstOrDefault(item => item.Id == id) is { } template
                => new TemplateRevisionsWindow(text, lab, template),
            RecoveryRoute.Revisions => new StorageWindow(text, lab),
            _ => new TroubleshootingWindow(text, lab)
        };
}
