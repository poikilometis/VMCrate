using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using VMCrate.Core;
using VMCrate.Windows;

namespace VMCrate.Desktop;

internal sealed class TroubleshootingWindow : WorkflowWindow
{
    internal TroubleshootingWindow(Labels text, LabService lab) : base(text, "tools.help", 715, 550)
    {
        var relevant = Field("label.history", new ListBox { Height = 205 });
        var actions = Actions();
        Action(actions, "help.guide", () =>
        {
            var path = LocalPaths.RequireFile(Path.Combine(AppContext.BaseDirectory, "docs", "operations.md"), ".md");
            using var reader = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return Task.CompletedTask;
        });
        async Task CheckAsync()
        {
            var readiness = await Task.Run(new PowerShellHypervisor().ReadHostReadinessAsync);
            var rows = new List<string>
            {
                $"VMCrate · {typeof(LabService).Assembly.GetName().Version?.ToString(3)} · Windows {readiness.Capacity?.WindowsBuild}",
                Text["setup." + readiness.HyperV]
            };
            if (readiness.Capacity?.RestartRequired == true)
                rows.Add(Text["setup.restartPending"]);
            try
            {
                var operations = await Task.Run(() => lab.ReadOperations());
                rows.AddRange(operations.Select(item => item.Code).OfType<string>().Reverse()
                    .Where(code => code is "error.buildBoot" or "error.isoMismatch" or "error.buildSpace" or
                        "error.updateSwitch" or "error.guestProbe" or "error.recoveryRequired").Distinct().Select(code => Text[code]));
            }
            catch (LabException error) when (error.Key is "error.state" or "error.stateVersion")
            {
                rows.Add(Text[error.Key]);
            }
            relevant.ItemsSource = rows;
        }
        Action(actions, "action.refresh", CheckAsync, true);
        Loaded += async (_, _) => await RunAsync(CheckAsync);
    }
}
