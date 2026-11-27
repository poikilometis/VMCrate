using System.Windows;
using System.Windows.Controls;
using VMCrate.Core;
using VMCrate.Windows;

namespace VMCrate.Desktop;

internal sealed class ConsoleToolsWindow : WorkflowWindow
{
    internal ConsoleToolsWindow(Labels text, LabService lab, Machine machine) : base(text, "tools.console", 680, 630)
    {
        Note(machine.Name);
        var details = Note("");
        var display = Note("");
        async Task CheckDisplaysAsync()
        {
            var local = VisualTreeHelperDpi();
            display.Text = $"Windows · {PowerShellHypervisor.MonitorCount} · " +
                $"{SystemParameters.VirtualScreenWidth:0} × {SystemParameters.VirtualScreenHeight:0} DIP · {local:P0}";
            var config = await Task.Run(new PowerShellHypervisor().ReadDisplaysAsync);
            display.Text += Environment.NewLine + (config.Video is { Length: > 0 } devices
                ? string.Join(Environment.NewLine, devices.Select(gpu =>
                    $"{gpu.Name} · {gpu.Driver ?? "—"} · {gpu.Width ?? 0} × {gpu.Height ?? 0}"))
                : Text["trace.check.Unknown"]);
        }
        var input = Field("console.note", new TextBox { AcceptsReturn = true, Height = 130, MaxLength = 64 * 1024,
            TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        HyperVIsolation? last = null;
        async Task RefreshAsync()
        {
            last = (await Task.Run(() => lab.ObserveIsolationAsync(machine.Id))).Host;
            details.Text = Text["power." + last.State] + Environment.NewLine +
                Text[last.EnhancedSessionAllowed == true ? "host.enhanced" : "isolation.redirect"] + Environment.NewLine +
                Text["isolation.inbox"] + " · " + Text[last.InboxAttached is null ? "trace.check.Unknown" :
                    last.InboxAttached == true ? "isolation.present" : "isolation.absent"];
        }

        var actions = Actions();
        Action(actions, "action.console", async () =>
        {
            await RefreshAsync();
            if (last is { State: PowerState.Running } &&
                (last.EnhancedSessionAllowed != true || Text.Confirm(this, "confirm.console")))
                await Task.Run(() => lab.OpenConsoleAsync(machine.Id));
        }, primary: true);
        Action(actions, "action.send", async () =>
        {
            var draft = input.Text;
            await Task.Run(() => lab.SendTextToInboxAsync(machine.Id, draft));
            input.Clear();
            await RefreshAsync();
        });
        Action(actions, "report.addFiles", async () =>
        {
            if (SelectFiles("report.addFiles") is not { Length: > 0 } files)
                return;
            await Task.Run(() => lab.SendFilesAsync(machine.Id, files));
            await RefreshAsync();
        });
        Action(actions, "tools.isolation", () =>
        {
            new IsolationWindow(Text, lab, [machine], machine.Id) { Owner = this }.ShowDialog();
            return Task.CompletedTask;
        });
        Action(actions, "console.displays", CheckDisplaysAsync);
        Action(actions, "action.ejectInbox", async () =>
        {
            await Task.Run(() => lab.EjectInboxAsync(machine.Id));
            await RefreshAsync();
        });
        Action(actions, "action.refresh", RefreshAsync);
        Loaded += async (_, _) => await RunAsync(RefreshAsync);
    }

    private double VisualTreeHelperDpi() => System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
}
