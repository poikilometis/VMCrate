using System.Windows.Controls;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class RevisionTrialWindow : WorkflowWindow
{
    internal RevisionTrialWindow(Labels text, LabService lab, Template source) : base(text, "revision.trial", 620, 570)
    {
        Note(Text.Format("revision.trialHint", source.Name));
        var name = Field("label.name", new TextBox());
        var revision = Field("label.revision", new TextBox());
        var ram = Field("label.memory", new TextBox { Text = "4" });
        var cpu = Field("label.cpu", new TextBox { Text = "2" });
        Action(Actions(), "action.create", async () =>
        {
            var values = (Name: name.Text, Revision: revision.Text, Ram: Integer(ram.Text), Cpu: Integer(cpu.Text));
            Queued = await Task.Run(() => lab.QueueRevisionTrial(source.Id, values.Name, values.Revision,
                new MachineOptions(values.Name, values.Ram, values.Cpu)));
            FinishDialog();
        }, primary: true);
    }

    internal TemplateBuild? Queued { get; private set; }
}
