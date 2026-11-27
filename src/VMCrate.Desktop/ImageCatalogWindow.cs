using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using VMCrate.Core;
using VMCrate.Windows;

namespace VMCrate.Desktop;

internal sealed record CatalogSelection(ImageSource Source, OfficialImage? Image = null, CachedInstaller? Cached = null);

internal sealed class ImageCatalogWindow : WorkflowWindow
{
    private readonly LabService lab;
    private readonly OfficialImages catalog = new(new PublisherSignatureVerifier());
    private readonly TextBox search;
    private readonly ComboBox sources;
    private readonly ListBox images;
    private readonly TextBlock details, transferStatus;
    private readonly ProgressBar progress;
    private readonly Button refresh, publisher, download, local;

    internal ImageCatalogWindow(Labels text, LabService lab, string? sourceId = null) : base(text, "tools.images", 800, 680)
    {
        this.lab = lab;
        Note(Text["hint.images"]);
        search = Field("images.search", new TextBox { MaxLength = 80 });
        sources = Field("images.source", new ComboBox { DisplayMemberPath = "Name" });
        var buttons = Actions();
        refresh = Action(buttons, "images.load", RefreshAsync);
        publisher = Action(buttons, "images.openPublisher", OpenPublisherAsync);
        local = Action(buttons, "images.useLocal", SelectLocalAsync);
        images = Field("images.variants", new ListBox { DisplayMemberPath = "FileName", Height = 180,
            FlowDirection = FlowDirection.LeftToRight });
        details = Note("");
        download = Action(Actions(), "images.download", DownloadAsync, true);
        Action(Actions(), "jobs.background", async () =>
        {
            if (images.SelectedItem is not OfficialImage image)
                return;
            await Task.Run(() => LocalJobProcess.Enqueue(lab, new(LocalJobKind.Download, Publisher: image.Source.Id, Digest: image.Sha256)));
            new LocalJobsWindow(Text, lab) { Owner = this }.ShowDialog();
        });
        transferStatus = Note("");
        progress = new ProgressBar { Height = 7, Visibility = Visibility.Collapsed };
        Body.Children.Add(progress);
        search.TextChanged += (_, _) => Filter();
        sources.SelectionChanged += (_, _) => ResetListing();
        images.SelectionChanged += (_, _) => SetButtons();
        Filter();
        if (sourceId is not null && OfficialImages.Sources.SingleOrDefault(item => item.Id == sourceId) is { } initial)
            sources.SelectedItem = initial;
        ResetListing();
    }

    internal CatalogSelection? Selection { get; private set; }
    private ImageSource? Source => sources.SelectedItem as ImageSource;

    private void Filter()
    {
        var previous = Source;
        var query = search.Text.Trim();
        var matching = OfficialImages.Sources.Where(item =>
            item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        sources.ItemsSource = matching;
        sources.SelectedItem = matching.Contains(previous) ? previous : matching.FirstOrDefault();
    }

    private void ResetListing()
    {
        images.ItemsSource = null;
        transferStatus.Text = "";
        details.Text = Source switch
        {
            { Automatic: true } => Text["images.loadHint"],
            not null => Text["images.manual"],
            _ => Text["images.noSources"]
        };
        SetButtons();
    }

    private void SetButtons()
    {
        refresh.IsEnabled = Source?.Automatic == true;
        publisher.IsEnabled = local.IsEnabled = Source is not null;
        download.IsEnabled = images.SelectedItem is OfficialImage;
        if (images.SelectedItem is OfficialImage selected)
            transferStatus.Text = Text["media." + (selected.Provenance?.Verification ?? MediaVerification.PublisherSha256)] +
                Environment.NewLine + $"SHA-256 {selected.Sha256}";
    }

    private Task RefreshAsync() => WithCancellationAsync(async cancellation =>
    {
        var source = Source!;
        var listing = await catalog.ListAsync(source.Id, cancellation);
        images.ItemsSource = listing.Images;
        images.SelectedIndex = listing.Images.Length > 0 ? 0 : -1;
        details.Text = listing.Images.Length == 0
            ? Text["images.none"] : Text.Format("images.count", listing.Images.Length);
        if (listing.FailedFeeds > 0)
            details.Text += Environment.NewLine + Text.Format("images.partial", listing.FailedFeeds);
    });

    private Task OpenPublisherAsync()
    {
        if (Source is { Page.Scheme: "https" } source)
        {
            using var browser = Process.Start(new ProcessStartInfo(source.Page.AbsoluteUri) { UseShellExecute = true });
        }
        return Task.CompletedTask;
    }

    private Task SelectLocalAsync()
    {
        Selection = new(Source!);
        FinishDialog();
        return Task.CompletedTask;
    }

    private Task DownloadAsync()
    {
        if (images.SelectedItem is not OfficialImage image)
            return Task.CompletedTask;

        return WithCancellationAsync(async cancellation =>
        {
            progress.Visibility = Visibility.Visible;
            progress.IsIndeterminate = true;
            var updates = new Progress<DownloadProgress>(RenderProgress);
            try
            {
                var cached = await Task.Run(() => lab.DownloadInstallerAsync(image, updates, cancellation), cancellation);
                Selection = new(image.Source, image, cached);
                FinishDialog();
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                throw new LabException("error.imageIncomplete");
            }
            finally { progress.Visibility = Visibility.Collapsed; }
        });
    }

    private void RenderProgress(DownloadProgress update)
    {
        progress.IsIndeterminate = update.TotalBytes is null || update.Verifying;
        if (update.TotalBytes is > 0)
            progress.Value = 100d * update.ReceivedBytes / update.TotalBytes.Value;
        transferStatus.Text = update.Verifying ? Text["images.verifying"]
            : Text.Format("images.received", MiB(update.ReceivedBytes),
                update.TotalBytes is long total ? MiB(total) : "—");
    }
}
