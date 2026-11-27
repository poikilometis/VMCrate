using System.Diagnostics;
using System.Windows.Controls;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class AppUpdateWindow : WorkflowWindow
{
    internal (ReleaseUpdate Update, bool Rollback)? Selection { get; private set; }

    internal AppUpdateWindow(Labels text, string workspace, ReleaseUpdate? active) : base(text, "appUpdate.title", 650, 470)
    {
        Note(Text["appUpdate.hint"]);
        var status = Note("");
        var actions = Actions();
        Button? activate = null;
        ReleaseUpdate? staged = null;
        Action(actions, "appUpdate.releases", () =>
        {
            using var browser = Process.Start(new ProcessStartInfo(ReleaseUpdate.ReleasesUrl) { UseShellExecute = true });
            return Task.CompletedTask;
        });
        Action(actions, "appUpdate.stage", () => SelectFile("appUpdate.stage", "ZIP (*.zip)|*.zip") is { } archive
            ? WithCancellationAsync(async token =>
            {
                staged = null;
                activate!.IsEnabled = false;
                status.Text = "";
                staged = await Task.Run(() => ReleaseUpdate.StageAsync(workspace, archive, token), token);
                status.Text = Text.Format("appUpdate.verified", staged.Version, staged.Sha256);
                activate.IsEnabled = true;
            }) : Task.CompletedTask, primary: true);
        activate = Action(actions, "appUpdate.activate", () =>
        {
            if (staged is not null && Text.Confirm(this, "appUpdate.confirm", staged.Version))
            {
                Selection = (staged, false);
                FinishDialog();
            }
            return Task.CompletedTask;
        });
        activate.IsEnabled = false;
        if (active is not null)
            Action(actions, "appUpdate.rollback", async () =>
            {
                if (!Text.Confirm(this, "appUpdate.confirmRollback"))
                    return;
                LocalPaths.RequireFile(active.PreviousExecutable, ".exe");
                await Task.Run(() => WorkspaceSchemaUpdate.Rollback(workspace, active.Id));
                Selection = (active, true);
                FinishDialog();
            });
    }
}
