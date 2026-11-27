using System.IO;
using System.Windows.Controls;
using Microsoft.Win32;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class LibraryCopyWindow : WorkflowWindow
{
    internal LibraryCopyWindow(Labels text, LabService lab) : base(text, "library.copy", 660, 480)
    {
        Note(Text["library.hint"]);
        var folder = Field("setup.workspace", new TextBox { IsReadOnly = true });
        var summary = Note("");
        LibraryCopyPlan? plan = null;
        var actions = Actions();
        var apply = Action(actions, "action.apply", () => WithCancellationAsync(async token =>
        {
            if (plan is not { } copy)
                return;
            await Task.Run(() => lab.CopyLibraryAsync(copy, token));
            summary.Text = Text["status.done"];
            plan = null;
        }), true);
        apply.IsEnabled = false;
        Action(actions, "action.browse", async () =>
        {
            var picker = new OpenFolderDialog { Title = Text["library.copy"] };
            if (picker.ShowDialog(this) != true)
                return;
            plan = null;
            apply.IsEnabled = false;
            var target = Path.Combine(picker.FolderName, $"VMCrate-{DateTime.Now:yyyyMMdd-HHmmss}");
            folder.Text = target;
            plan = await Task.Run(() => lab.PreviewLibraryCopy(target));
            summary.Text = Text.Format("library.summary", plan.Templates, plan.Images, plan.Profiles, MiB(plan.Bytes));
            apply.IsEnabled = true;
        });
    }
}
