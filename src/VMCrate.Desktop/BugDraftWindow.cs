using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class BugDraftWindow : WorkflowWindow
{
    internal BugDraftWindow(Labels text, SupportPackage report) : base(text, "support.bugDraft", 740, 620)
    {
        Note(Text["support.bugHint"]);
        var draft = Field("support.bugDraft", new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 350,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Text = $"## {Text["support.steps"]}\n\n1. \n\n## {Text["support.expected"]}\n\n\n" +
                $"## {Text["support.actual"]}\n\n\n## VMCrate {report.VmCrateVersion}\n\n" +
                $"```json\n{report.Preview()}\n```\n"
        });
        Action(Actions(), "action.save", async () =>
        {
            if (SaveFile("support.bugDraft", "vmcrate-issue.md", ".md") is not { } output)
                return;
            var content = draft.Text;
            await Task.Run(() => OutputFile.WriteNew(output, ".md", stream =>
            {
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
                writer.Write(content);
            }));
        }, primary: true);
    }
}
