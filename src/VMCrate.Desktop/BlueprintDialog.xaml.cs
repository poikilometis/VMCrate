using System.Windows;
using VMCrate.Core;

namespace VMCrate.Desktop;

public partial class BlueprintDialog : Window
{
    internal BlueprintDialog(Labels labels, BlueprintPreview plan)
    {
        InitializeComponent();
        DataContext = labels;
        labels.Apply(this);
        Title = labels["tools.blueprint"];
        var existing = plan.Machines.Count(item => item.AlreadyCreated);
        Summary.Text = labels.Format("blueprint.summary", plan.Machines.Count - existing, existing);
        DeployButton.IsEnabled = existing != plan.Machines.Count;
        PlanRows.ItemsSource = plan.Machines.Select(vm =>
        {
            var network = vm.Segment is null ? labels["network.Offline"] : labels.Format("blueprint.segment", vm.Segment);
            var status = labels[vm.AlreadyCreated ? "blueprint.existing" : "blueprint.new"];
            return new PreviewRow(vm.Name, labels.Format("detail.resources", vm.Template, vm.MemoryGiB, vm.CpuCount) +
                $" · {network} · {status}");
        }).ToArray();
    }

    private void OnDeploy(object sender, RoutedEventArgs e) => DialogResult = true;

    private sealed record PreviewRow(string Name, string Details);
}
