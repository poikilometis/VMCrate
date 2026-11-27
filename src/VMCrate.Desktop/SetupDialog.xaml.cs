using System.Globalization;
using System.Windows;
using Microsoft.Win32;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal enum SetupKind { Installer, Clone, Import, Capture, Disposable }

public partial class SetupDialog : Window
{
    private readonly Labels labels;
    private readonly SetupKind kind;

    internal MachineOptions Options { get; private set; } = new("", 4, 2);
    internal int DiskGiB { get; private set; } = 64;
    internal GuestFamily Guest => GuestPicker.SelectedValue is GuestFamily value ? value : GuestFamily.Windows;
    internal bool SecureBoot => SecureBootBox.IsChecked == true;
    internal string MediaPath => MediaInput.Text;

    internal SetupDialog(Labels labels, SetupKind kind)
    {
        this.labels = labels;
        this.kind = kind;
        InitializeComponent();
        DataContext = labels;
        labels.Apply(this);

        var (action, hint) = kind switch
        {
            SetupKind.Installer => ("action.newInstaller", "hint.installer"),
            SetupKind.Clone => ("action.clone", "hint.clone"),
            SetupKind.Disposable => ("action.disposable", "hint.disposable"),
            SetupKind.Import => ("action.importTemplate", "hint.import"),
            _ => ("action.capture", "hint.capture")
        };
        Title = labels[action];
        DialogHeading.Text = Title;
        DialogHint.Text = labels[hint];
        var allocateResources = kind is SetupKind.Installer or SetupKind.Clone or SetupKind.Disposable;
        AcceptButton.Content = labels[allocateResources ? "action.create" : "action.save"];
        GuestFields.Visibility = VisibleWhen(kind is SetupKind.Installer or SetupKind.Import);
        MediaFields.Visibility = GuestFields.Visibility;
        ResourceFields.Visibility = VisibleWhen(allocateResources);
        DiskFields.Visibility = VisibleWhen(kind == SetupKind.Installer);
        MediaLabel.Text = labels[kind == SetupKind.Installer ? "label.iso" : "label.vhdx"];
        GuestPicker.ItemsSource = Enum.GetValues<GuestFamily>()
            .Select(guest => new GuestChoice(guest, labels["guest." + guest])).ToArray();
        GuestPicker.SelectedValue = GuestFamily.Windows;
        Loaded += (_, _) => NameInput.Focus();
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var extension = kind == SetupKind.Installer ? "iso" : "vhdx";
        var picker = new OpenFileDialog
        {
            Title = MediaLabel.Text,
            Filter = $"{extension.ToUpperInvariant()} (*.{extension})|*.{extension}",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) == true)
            MediaInput.Text = picker.FileName;
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        int memory = 4, cpu = 2, disk = 64;
        if (string.IsNullOrWhiteSpace(NameInput.Text))
        {
            ShowValidation("error.name");
            return;
        }
        if (MediaFields.Visibility == Visibility.Visible && string.IsNullOrEmpty(MediaPath))
        {
            ShowValidation("error.file");
            return;
        }
        if (ResourceFields.Visibility == Visibility.Visible &&
            (!ParseInteger(MemoryInput.Text, out memory) || memory is < 2 or > 128 ||
             !ParseInteger(CpuInput.Text, out cpu) || cpu is < 1 or > 32))
        {
            ShowValidation("error.resources");
            return;
        }
        if (DiskFields.Visibility == Visibility.Visible &&
            (!ParseInteger(DiskInput.Text, out disk) || disk is < 16 or > 2048))
        {
            ShowValidation("error.diskSize");
            return;
        }
        Options = new MachineOptions(NameInput.Text, memory, cpu);
        DiskGiB = disk;
        DialogResult = true;
    }

    private static bool ParseInteger(string input, out int value)
    {
        var characters = input.Select(c => CharUnicodeInfo.GetDecimalDigitValue(c) is int digit and >= 0
            ? (char)('0' + digit) : c).ToArray();
        return int.TryParse(characters.AsSpan(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private void ShowValidation(string key)
    {
        ValidationMessage.Text = labels[key];
        ValidationMessage.Visibility = Visibility.Visible;
    }

    private static Visibility VisibleWhen(bool condition) => condition ? Visibility.Visible : Visibility.Collapsed;
    private sealed record GuestChoice(GuestFamily Guest, string Name);
}
