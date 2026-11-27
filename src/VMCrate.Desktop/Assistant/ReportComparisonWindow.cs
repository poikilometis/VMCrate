using System.Windows;
using System.Windows.Controls;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class ReportComparisonWindow : WorkflowWindow
{
    internal ReportComparisonWindow(Labels text, IReadOnlyList<ReportChange> changes, string title = "report.compare")
        : base(text, title, 820, 590)
    {
        if (changes.Count == 0)
            Note(Text["report.noChanges"]);
        foreach (var change in changes)
        {
            Note(Text[change.Field]);
            Body.Children.Add(new TextBox
            {
                Text = $"{change.Before ?? "—"}{Environment.NewLine}↓{Environment.NewLine}{change.After ?? "—"}",
                IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 150,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            });
        }
    }
}
