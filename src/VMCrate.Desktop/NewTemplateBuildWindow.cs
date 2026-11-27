using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class NewTemplateBuildWindow : WorkflowWindow
{
    internal NewTemplateBuildWindow(Labels text, LabService lab, CatalogSelection? selection = null, Template? parent = null) : base(text, "action.newBuild", 630, 725)
    {
        var suggested = selection?.Image is { } image ? Path.GetFileNameWithoutExtension(image.FileName) : selection?.Source.Name ?? "";
        var next = parent is null ? "" : DateTime.UtcNow.ToString("yyyyMMdd-HHmm", System.Globalization.CultureInfo.InvariantCulture);
        if (parent is not null)
            suggested = $"{parent.Name[..Math.Min(45, parent.Name.Length)]} {next}";
        var name = Field("label.name", new TextBox { MaxLength = 64, Text = suggested[..Math.Min(64, suggested.Length)] });
        var revision = Field("label.revision", new TextBox { MaxLength = 64, Text = next });
        var guest = Field("label.guest", new ComboBox { DisplayMemberPath = "Name", SelectedValuePath = "Family" });
        guest.ItemsSource = Enum.GetValues<GuestFamily>().Select(family => new GuestChoice(family, Text["guest." + family]));
        guest.SelectedValue = parent?.Guest ?? selection?.Source.Guest ?? GuestFamily.Windows;
        guest.IsEnabled = parent is null;
        var boot = new CheckBox { Content = Text["label.secureBoot"], IsChecked = parent?.SecureBoot ?? selection?.Source.SecureBoot ?? true, Margin = new Thickness(0, 0, 0, 12) };
        Body.Children.Add(boot);

        var iso = ChooseMedia("label.iso", "ISO (*.iso)|*.iso");
        iso.Text = selection?.Cached?.Path ?? "";
        var hash = Field("label.isoHash", new TextBox
        {
            MaxLength = 64, FlowDirection = FlowDirection.LeftToRight, Text = selection?.Cached?.Sha256 ?? ""
        });
        iso.TextChanged += (_, _) => hash.Clear();
        if (selection?.Cached is null)
        {
            Note(Text["hint.localIsoHash"]);
            Action(Actions(), "images.calculateHash", () => WithCancellationAsync(async cancellation =>
            {
                var path = iso.Text;
                hash.Text = await Task.Run(() => lab.CalculateLocalIsoDigestAsync(path, cancellation), cancellation);
            }));
        }
        var start = Body.Children.Count;
        var answer = ChooseMedia("label.answerFile", "Autounattend.xml|Autounattend.xml");
        var windowsFields = Body.Children.Cast<UIElement>().Skip(start).ToArray();
        start = Body.Children.Count;
        var seed = ChooseMedia("build.seed", "ISO (*.iso)|*.iso");
        var seedHash = Field("label.isoHash", new TextBox { MaxLength = 64, FlowDirection = FlowDirection.LeftToRight });
        Action(Actions(), "kali.seedAction", () =>
        {
            var generator = new KaliSeedWindow(Text, iso.Text, hash.Text) { Owner = this };
            if (generator.ShowDialog() == true && generator.Destination is { } generated)
            {
                seed.Text = generated;
                seedHash.Text = generator.Digest;
                boot.IsChecked = false;
            }
            return Task.CompletedTask;
        });
        iso.TextChanged += (_, _) =>
        {
            seed.Clear();
            seedHash.Clear();
        };
        seed.TextChanged += (_, _) => seedHash.Clear();
        var linuxFields = Body.Children.Cast<UIElement>().Skip(start).ToArray();
        void RenderMedia()
        {
            var windows = guest.SelectedValue is GuestFamily.Windows;
            foreach (var item in windowsFields)
                item.Visibility = windows ? Visibility.Visible : Visibility.Collapsed;
            foreach (var item in linuxFields)
                item.Visibility = windows ? Visibility.Collapsed : Visibility.Visible;
        }
        guest.SelectionChanged += (_, _) => RenderMedia();
        RenderMedia();
        var memory = Field("label.memory", new TextBox { Text = "4" });
        var cpu = Field("label.cpu", new TextBox { Text = "2" });
        var disk = Field("label.disk", new TextBox { Text = "64" });
        var setup = Actions();
        var space = Note("");
        Action(setup, "build.checkSpace", async () =>
        {
            var options = (Iso: iso.Text, Hash: hash.Text, Disk: Integer(disk.Text));
            var estimate = await Task.Run(() => lab.EstimateBuildSpace(options.Iso, options.Hash, options.Disk));
            var values = new[] { estimate.FreeBytes, estimate.CacheBytes, estimate.FullDiskGrowthBytes }
                .Select(bytes => bytes is long length ? MiB(length / 1024) : "—").ToArray();
            space.Text = Text.Format("build.space", values[0], values[1], values[2]);
            if (estimate.CacheBytes is long required && estimate.FreeBytes is long available && required > available)
                space.Text += Environment.NewLine + Text["error.buildSpace"];
        });
        Action(setup, "action.create", async () =>
        {
            var options = new TemplateBuildOptions(name.Text, revision.Text,
                new InstallerOptions(new MachineOptions(name.Text, Integer(memory.Text), Integer(cpu.Text)),
                    (GuestFamily)guest.SelectedValue, boot.IsChecked == true, iso.Text, Integer(disk.Text)),
                hash.Text, guest.SelectedValue is GuestFamily.Windows ? answer.Text : seed.Text, parent?.Id,
                Media: selection?.Cached is { } cached && cached.Path == iso.Text &&
                    cached.Sha256.Equals(hash.Text.Trim(), StringComparison.OrdinalIgnoreCase) ? cached.Provenance : null,
                BootSeedHash: guest.SelectedValue is GuestFamily.Linux ? seedHash.Text : null, VerifyCloneOnPublish: true);
            await Task.Run(() => lab.QueueBuild(options));
            Created = true;
            FinishDialog();
        }, true);
    }

    internal bool Created { get; private set; }

    private TextBox ChooseMedia(string key, string filter)
    {
        var path = Field(key, new TextBox { IsReadOnly = true, FlowDirection = FlowDirection.LeftToRight });
        Action(Actions(), "action.browse", () =>
        {
            var picker = new OpenFileDialog { Filter = filter, CheckFileExists = true, Title = Text[key] };
            if (picker.ShowDialog(this) == true)
                path.Text = picker.FileName;
            return Task.CompletedTask;
        });
        return path;
    }

    private sealed record GuestChoice(GuestFamily Family, string Name);
}
