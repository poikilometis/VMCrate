using System.Windows.Controls;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class BuildQueueWindow : WorkflowWindow
{
    internal BuildQueueWindow(Labels text, LabService lab) : base(text, "queue.title", 675, 530)
    {
        Note(Text["queue.hint"]);
        var memory = Field("label.memory", new TextBox { Text = "16" });
        var cpu = Field("label.cpu", new TextBox { Text = "8" });
        var disk = Field("label.disk", new TextBox { Text = "256" });
        var history = Field("label.history", new ListBox { Height = 140 });
        Action(Actions(), "action.continue", () =>
        {
            var limits = new BuildQueueLimits(Integer(memory.Text), Integer(cpu.Text), Integer(disk.Text) * 1024L * 1024 * 1024);
            return WithCancellationAsync(async token =>
            {
                history.Items.Clear();
                while (await Task.Run(() => lab.PrepareNextQueuedBuildAsync(limits, token)) is { } step)
                {
                    history.Items.Add($"{step.Name} · {Text[step.BlockedBy ?? "build." + step.Stage]}");
                    if (step.BlockedBy is not null)
                        return;
                    token.ThrowIfCancellationRequested();
                }
            });
        }, primary: true);
    }
}
