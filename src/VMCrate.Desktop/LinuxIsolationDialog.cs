using System.Windows;
using System.Windows.Controls;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class LinuxIsolationDialog : WorkflowWindow
{
    internal LinuxIsolationDialog(Labels text, LabService lab, Guid machine, Action<IsolationObservation> show)
        : base(text, "action.guestProbe", 630, 480)
    {
        Note(text["isolation.linuxHint"]);
        Field("trace.mountProbe", new TextBox
        {
            Text = "sh /media/cdrom/VMCrate-isolation.sh", IsReadOnly = true, FlowDirection = FlowDirection.LeftToRight
        });
        Action(Actions(), "trace.mountProbe", async () =>
        {
            if (Text.Confirm(this, "confirm.mountProbe"))
                await Task.Run(() => lab.MountLinuxIsolationProbeAsync(machine));
        });
        var result = Field("trace.resultCode", new TextBox { MaxLength = 128, FlowDirection = FlowDirection.LeftToRight });
        Action(Actions(), "trace.readResult", async () =>
        {
            var value = result.Text;
            show(await Task.Run(() => lab.ReadLinuxIsolationAsync(machine, value)));
            FinishDialog();
        }, primary: true);
    }
}
