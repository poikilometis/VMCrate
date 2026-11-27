namespace VMCrate.Core;

public enum RecoveryRoute { Help, Host, Workspace, Resources, Proxy, Jobs, Backup, Images, Revisions }

public static class FailureRouting
{
    public static RecoveryRoute From(string? code) => code switch
    {
        "error.host" or "error.backend" => RecoveryRoute.Host,
        "error.state" or "error.stateVersion" or "error.layout" or "error.machine" or
            "error.recoveryRequired" or "error.recoveryChanged" => RecoveryRoute.Workspace,
        "error.buildSpace" or "error.resources" or "error.resourcesBusy" => RecoveryRoute.Resources,
        "error.proxyDrift" or "error.proxyLease" or "error.updateAcl" or "error.updateEndpoint" or
            "status.proxyExpired" or "error.proxyResult" => RecoveryRoute.Proxy,
        "error.job" or "error.busy" => RecoveryRoute.Jobs,
        "error.backupFormat" or "error.labChanged" or "error.drillPath" => RecoveryRoute.Backup,
        "error.isoMismatch" or "error.imageSource" => RecoveryRoute.Images,
        "error.projectIntegrity" => RecoveryRoute.Revisions,
        _ => RecoveryRoute.Help
    };
}
