using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;

namespace VMCrate.Desktop;

internal abstract class WorkflowWindow : Window
{
    protected readonly Labels Text;
    protected readonly StackPanel Body = new() { Margin = new Thickness(20) };
    private readonly TextBlock status = new() { Margin = new Thickness(20, 0, 20, 12) };
    private bool busy;
    private readonly DockPanel footer = new();
    private CancellationTokenSource? cancellation;
    private Button? cancel;

    protected WorkflowWindow(Labels text, string title, double width = 760, double height = 610)
    {
        Text = text;
        Title = text[title];
        text.Apply(this);
        Style = (Style)Application.Current.FindResource(typeof(Window));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Width = width;
        Height = height;
        MinWidth = 480;
        MinHeight = 400;

        var layout = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        footer.Children.Add(status);
        layout.Children.Add(footer);
        layout.Children.Add(new ScrollViewer { Content = Body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = layout;
        Body.Children.Add(new TextBlock
        {
            Text = Title, Margin = new Thickness(0, 0, 0, 12), Style = (Style)FindResource("Heading")
        });
        Closing += (_, e) => e.Cancel = busy;
    }

    protected TextBlock Note(string value)
    {
        var note = new TextBlock { Text = value, Margin = new Thickness(0, 0, 0, 8) };
        Body.Children.Add(note);
        return note;
    }

    protected WrapPanel Actions()
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 4, 0, 2) };
        Body.Children.Add(panel);
        return panel;
    }

    protected T Field<T>(string label, T control) where T : FrameworkElement
    {
        Note(Text[label]);
        AutomationProperties.SetName(control, Text[label]);
        control.Margin = new Thickness(0, 0, 0, 8);
        Body.Children.Add(control);
        return control;
    }

    protected Button Action(WrapPanel panel, string key, Func<Task> action, bool primary = false)
    {
        var button = new Button { Content = Text[key] };
        if (primary)
            button.Style = (Style)FindResource("PrimaryButton");
        button.Click += async (_, _) => await RunAsync(action);
        panel.Children.Add(button);
        return button;
    }

    protected async Task RunAsync(Func<Task> action, bool quiet = false)
    {
        if (busy)
            return;
        busy = true;
        Body.IsEnabled = false;
        status.Text = Text["status.working"];
        try
        {
            await action();
            if (status.Text == Text["status.working"])
                status.Text = string.Empty;
        }
        catch (Exception error)
        {
            status.Text = Text["status.error"];
            if (!quiet)
                Text.ShowError(this, error);
        }
        finally
        {
            busy = false;
            Body.IsEnabled = true;
        }
    }

    protected async Task WithCancellationAsync(Func<CancellationToken, Task> action)
    {
        if (cancel is null)
        {
            cancel = new Button { Content = Text["action.cancel"], Visibility = Visibility.Collapsed };
            DockPanel.SetDock(cancel, Dock.Right);
            cancel.Click += (_, _) => cancellation?.Cancel();
            footer.Children.Insert(0, cancel);
        }

        using var source = new CancellationTokenSource();
        cancellation = source;
        cancel.Visibility = Visibility.Visible;
        try { await action(source.Token); }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            status.Text = Text["status.cancelled"];
        }
        finally
        {
            cancel.Visibility = Visibility.Collapsed;
            cancellation = null;
        }
    }

    protected void FinishDialog()
    {
        busy = false;
        DialogResult = true;
    }

    protected string? SaveFile(string title, string suggestedName, string extension = ".json")
    {
        var type = extension[1..].ToUpperInvariant();
        var dialog = new SaveFileDialog
        {
            Title = Text[title], FileName = suggestedName, DefaultExt = extension,
            Filter = $"{type} (*{extension})|*{extension}", AddExtension = true, OverwritePrompt = false
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    protected string? SelectFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = Text[title], Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    protected string[]? SelectFiles(string title)
    {
        var dialog = new OpenFileDialog { Title = Text[title], Multiselect = true, CheckFileExists = true };
        return dialog.ShowDialog(this) == true ? dialog.FileNames : null;
    }

    protected static string MiB(long bytes) => (bytes / 1048576d).ToString("N1", CultureInfo.CurrentCulture);

    protected static int Integer(string value)
    {
        var normalized = value.Select(character => CharUnicodeInfo.GetDecimalDigitValue(character) is int digit and >= 0
            ? (char)('0' + digit) : character).ToArray();
        return int.TryParse(normalized.AsSpan(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;
    }
}
