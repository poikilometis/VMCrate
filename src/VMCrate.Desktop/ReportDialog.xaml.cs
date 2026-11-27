using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VMCrate.Core;

namespace VMCrate.Desktop;

public partial class ReportDialog : Window
{
    private readonly Labels labels;
    internal IReadOnlyList<string> Files { get; private set; } = [];
    internal Guid? MachineId => ReportMachine.SelectedValue is Guid id && id != Guid.Empty ? id : null;

    internal ReportDialog(Labels labels, IReadOnlyList<Machine> machines, Guid? selected)
    {
        this.labels = labels;
        InitializeComponent();
        DataContext = labels;
        labels.Apply(this);
        Title = labels["tools.report"];
        ReportMachine.ItemsSource = machines.OrderBy(vm => vm.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(vm => new MachineChoice(vm.Id, vm.Name))
            .Prepend(new MachineChoice(Guid.Empty, labels["report.noMachine"])).ToArray();
        ReportMachine.SelectedValue = selected ?? Guid.Empty;
        Render();
    }

    private void OnSelectFiles(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = labels["report.addFiles"], Multiselect = true, CheckFileExists = true };
        if (picker.ShowDialog(this) == true)
        {
            Files = picker.FileNames;
            Render();
        }
    }

    private void OnClearFiles(object sender, RoutedEventArgs e)
    {
        Files = [];
        Render();
    }

    private void OnMachineChanged(object sender, SelectionChangedEventArgs e) => Render();
    private void OnSave(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Render()
    {
        FileSummary.Text = labels.Format("report.files", Files.Count);
        ClearButton.IsEnabled = Files.Count > 0;
        SaveButton.IsEnabled = MachineId is not null || Files.Count > 0;
    }

    private sealed record MachineChoice(Guid Id, string Name);
}
