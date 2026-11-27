using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using VMCrate.Core;
using VMCrate.Desktop;
using static VMCrate.Tests.Checks;

namespace VMCrate.Tests;

internal static class LocalizationChecks
{
    internal static void Catalogs()
    {
        var english = Load("en");
        Expect(TextCatalog.Locales.Count == 15 && TextCatalog.Locales.Count(locale => locale.RightToLeft) == 3,
            "Unexpected locale coverage.");
        foreach (var locale in TextCatalog.Locales)
        {
            var translated = Load(locale.Id);
            var catalog = new TextCatalog(locale.Id);
            Expect(english.Keys.ToHashSet().SetEquals(translated.Keys), $"Wrong key set for {locale.Id}.");
            foreach (var status in Enum.GetValues<RemoteJobStatus>())
                Expect(translated.ContainsKey("remote." + status), $"Missing remote job status {status} in {locale.Id}.");
            foreach (var stage in Enum.GetValues<BuildStage>())
                Expect(translated.ContainsKey("build." + stage), $"Missing stage {stage} in {locale.Id}.");
            foreach (var (key, value) in english)
            {
                Expect(!string.IsNullOrWhiteSpace(translated[key]), $"Blank string {locale.Id}:{key}.");
                Expect(Placeholders(value).SequenceEqual(Placeholders(translated[key])), $"Placeholder mismatch {locale.Id}:{key}.");
                Expect(catalog[key] == translated[key], $"Unexpected fallback for {locale.Id}:{key}.");
                catalog.Format(key, "name", 4, 2, "relay");
            }
        }
        Expect(TextCatalog.Resolve("ru-RU").Id == "ru", "Russian subtag was not resolved.");
        Expect(TextCatalog.Resolve("ar-EG").Id == "arz", "Egyptian Arabic subtag was not resolved.");
        Expect(TextCatalog.Resolve("zh-CN").Id == "zh-Hans", "Mandarin subtag was not resolved.");
        Expect(TextCatalog.Resolve("xx").Id == "en", "Unknown language did not fall back to English.");
    }

    internal static void Windows()
    {
        using var temp = new TemporaryDirectory();
        var iso = temp.File("fixture.iso", "test");
        var app = new App();
        app.InitializeComponent();
        var labels = new Labels(new TextCatalog("en"));
        var window = new WorkspaceWindow(new LabService(temp.Root, new FakeHypervisor(), new FakeInbox()),
            labels, new Preferences(temp.Root));
        var layout = (FrameworkElement)window.Content;
        layout.Measure(new Size(1080, 740));
        layout.Arrange(new Rect(0, 0, 1080, 740));
        layout.UpdateLayout();
        var picker = (ComboBox)window.FindName("LanguagePicker");
        var refresh = (Button)window.FindName("RefreshButton");
        foreach (var locale in TextCatalog.Locales)
        {
            picker.SelectedValue = locale.Id;
            refresh.GetBindingExpression(ContentControl.ContentProperty)?.UpdateTarget();
            var direction = locale.RightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            Expect(window.FlowDirection == direction, $"Wrong flow direction for {locale.Id}.");
            Expect(Equals(refresh.Content, labels["action.refresh"]), $"Refresh label for {locale.Id} is [{refresh.Content}], expected [{labels["action.refresh"]}]. Context: {refresh.DataContext}, binding: {refresh.GetBindingExpression(ContentControl.ContentProperty)?.Status}.");
            var dialog = new SetupDialog(labels, SetupKind.Installer);
            Expect(dialog.FlowDirection == direction && dialog.Title == labels["action.newInstaller"],
                $"Installer dialog did not localize for {locale.Id}.");
            var planDialog = new BlueprintDialog(labels, new BlueprintPreview(
                [new("Endpoint", "Example", 4, 2, "isolated", false)]));
            var reportDialog = new ReportDialog(labels, [], null);
            var sessionDialog = new SetupDialog(labels, SetupKind.Disposable);
            Expect(planDialog.FlowDirection == direction && planDialog.Title == labels["tools.blueprint"],
                $"Blueprint dialog did not localize for {locale.Id}.");
            Expect(reportDialog.FlowDirection == direction && reportDialog.Title == labels["tools.report"],
                $"Report dialog did not localize for {locale.Id}.");
            Expect(sessionDialog.FlowDirection == direction && sessionDialog.Title == labels["action.disposable"],
                $"Disposable dialog did not localize for {locale.Id}.");
            var lab = new LabService(temp.Root, new FakeHypervisor(), new FakeInbox());
            System.Windows.Window[] workflows =
            [
                new HostDiagnosticsWindow(labels, lab), new TemplateBuildWindow(labels, lab),
                new IsolationWindow(labels, lab, [], null), new IsolationGuestDialog(labels, (_, _) => Task.CompletedTask),
                new SupportWindow(labels, lab, []), new NewTemplateBuildWindow(labels, lab),
                new DiskRecoveryWindow(labels, lab, new RecoveryPointScope(Guid.NewGuid())),
                new BuildQueueWindow(labels, lab), new LibraryCopyWindow(labels, lab), new RecipeWindow(labels, lab, []),
                new NetworkTopologyWindow(labels, lab, "fixture"), new TroubleshootingWindow(labels, lab),
                new ReportComparisonWindow(labels, []),
                new LabGroupWindow(labels, lab), new StorageWindow(labels, lab), new CleanSessionWindow(labels, lab, temp.Root), new SessionProfilesWindow(labels, lab, temp.Root, null, new("", 4, 2)),
                new FirstRunWindow(labels, temp.Root, () => Task.FromResult(new HostReadiness(HyperVReadiness.Ready, true, false, null)), null),
                new FirstMachineWindow(labels, lab, temp.Root, () => Task.FromResult(new HostReadiness(HyperVReadiness.Ready, true, false, null))),
                new LabBackupWindow(labels, lab, []), new BuildDiagnosticsWindow(labels, lab, Guid.NewGuid()),
                new LocalJobsWindow(labels, lab), new OutboxWindow(labels, lab), new ResourcePlanWindow(labels, lab),
                new LinuxIsolationDialog(labels, lab, Guid.NewGuid(), _ => { }), new AppUpdateWindow(labels, temp.Root, null),
                new RevisionTrialWindow(labels, lab, new Template(Guid.NewGuid(), "Base", GuestFamily.Linux, false)),
                new RecoveryWindow(labels, lab), new ImageCatalogWindow(labels, lab),
                new AssistantSettingsWindow(labels), new DescriptionBlueprintWindow(labels, lab, []),
                new SessionReportWindow(labels, lab), new UpdateAdvisorWindow(labels, lab, []),
                new ProjectWindow(labels, lab), new OperationJournalWindow(labels, lab),
                new KaliSeedWindow(labels, iso, new string('0', 64)), new RemoteQueueWindow(labels),
                new MachineTransferWindow(labels, lab, null),
                new LinuxInventoryWindow(labels, lab, new Template(Guid.NewGuid(), "Linux", GuestFamily.Linux, false)),
                new TemplateRevisionsWindow(labels, lab, new Template(Guid.NewGuid(), "Base", GuestFamily.Linux, false)),
                new ProxyTraceWindow(labels, lab, new Machine(Guid.NewGuid(), "Builder", GuestFamily.Linux, false, null, 4, 2, NetworkMode.Updates)),
                new ProxyTraceWindow(labels, lab, new Machine(Guid.NewGuid(), "Builder", GuestFamily.Windows, true, null, 4, 2, NetworkMode.Updates))
            ];
            foreach (var workflow in workflows)
            {
                var surface = (FrameworkElement)workflow.Content;
                surface.Measure(new Size(520, 420));
                surface.Arrange(new Rect(0, 0, 520, 420));
            }
            Expect(workflows.All(item => item.FlowDirection == direction &&
                !item.Title.StartsWith("tools.") && !item.Title.StartsWith("outbox.") && !item.Title.StartsWith("appUpdate.")),
                $"Lab workflow localization failed for {locale.Id}.");
        }
        Expect(new Preferences(temp.Root).ReadLanguage() == "arz", "Language selection did not persist.");
    }

    private static Dictionary<string, string> Load(string id)
    {
        using var stream = typeof(TextCatalog).Assembly.GetManifestResourceStream($"VMCrate.Core.Locales.{id}.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }

    private static IEnumerable<string> Placeholders(string text) =>
        Regex.Matches(text, "\\{[0-9]+\\}").Select(match => match.Value).Order();
}
