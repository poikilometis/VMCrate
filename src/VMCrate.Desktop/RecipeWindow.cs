using System.IO;
using System.Windows.Controls;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class RecipeWindow : WorkflowWindow
{
    internal RecipeWindow(Labels text, LabService lab, IReadOnlyList<Template> templates)
        : base(text, "recipes.title", 720, 550)
    {
        var choices = Field("recipes.title", new ListBox { DisplayMemberPath = nameof(LabRecipe.Name), Height = 164 });
        var revisionSource = Field("label.template", new ComboBox { DisplayMemberPath = "Name" });
        revisionSource.ItemsSource = templates.Where(item => item.Guest == GuestFamily.Linux && item.Sha256 is not null)
            .OrderByDescending(item => item.Preferred).ThenByDescending(item => item.CreatedUtc).ToArray();
        revisionSource.SelectedIndex = 0;
        var overview = Note("");
        var controls = Actions();
        var action = Action(controls, "action.apply", async () =>
        {
            if (choices.SelectedItem is not LabRecipe recipe)
                return;
            if (recipe.Blueprint is { } blueprint)
            {
                await WithCancellationAsync(async token =>
                {
                    var plan = await Task.Run(() => OfflinePlanning.PlanAsync(lab.WorkspaceDirectory, blueprint, cancellation: token), token);
                    overview.Text = Describe(recipe) + Environment.NewLine + string.Join(Environment.NewLine,
                        plan.Templates.Select(item => $"{item.Name} · {item.Revision ?? "—"} · {item.Sha256?[..12] ?? "—"} · {Text["recipes." + item.Status]}"));
                    if (plan.NameConflicts.Length > 0)
                        overview.Text += Environment.NewLine + Text["error.blueprintConflict"] + " · " + string.Join(", ", plan.NameConflicts);
                    if (!plan.Ready)
                        return;
                    var preview = await Task.Run(() => lab.PreviewBlueprintAsync(blueprint), token);
                    if (new BlueprintDialog(Text, preview) { Owner = this }.ShowDialog() != true)
                        return;
                    var pins = OfflinePlanning.Pin(blueprint, plan);
                    if (recipe.Id == "windows-offline" && blueprint.Machines is [var vm] && vm.Segment is null &&
                        templates.SingleOrDefault(item => item.Name.Equals(vm.Template, StringComparison.OrdinalIgnoreCase)) is { } selected)
                    {
                        await Task.Run(() => lab.PrepareCleanSessionAsync(selected.Id, new(vm.Name, vm.MemoryGiB, vm.CpuCount), [],
                            expectedSha256: pins.Templates[0].Sha256), token);
                        new CleanSessionWindow(Text, lab, lab.WorkspaceDirectory) { Owner = this }.ShowDialog();
                    }
                    else
                        await Task.Run(() => lab.RunPinnedLabAsync(blueprint, pins, RemoteLabAction.Deploy, token), token);
                });
            }
            else if (recipe.PrivateUpdates && revisionSource.SelectedItem is Template source)
            {
                var trial = new RevisionTrialWindow(Text, lab, source) { Owner = this };
                if (trial.ShowDialog() == true && trial.Queued is { } job)
                    new TemplateBuildWindow(Text, lab, job.Id) { Owner = this }.ShowDialog();
            }
            else
            {
                var images = new ImageCatalogWindow(Text, lab, recipe.PublisherId) { Owner = this };
                if (images.ShowDialog() == true && images.Selection is { } selection &&
                    new NewTemplateBuildWindow(Text, lab, selection) { Owner = this }.ShowDialog() == true)
                    new TemplateBuildWindow(Text, lab) { Owner = this }.ShowDialog();
            }
        }, true);
        Action(controls, "tools.blueprint", () =>
        {
            if ((choices.SelectedItem as LabRecipe)?.Blueprint is { } selected)
                new BlueprintEditorWindow(Text, lab, templates, selected) { Owner = this }.ShowDialog();
            return Task.CompletedTask;
        });
        action.IsEnabled = false;
        choices.SelectionChanged += (_, _) =>
        {
            action.IsEnabled = choices.SelectedItem is LabRecipe;
            if (choices.SelectedItem is not LabRecipe item)
                return;
            revisionSource.IsEnabled = item.PrivateUpdates;
            overview.Text = Describe(item);
            if (item.Blueprint is { } blueprint)
            {
                var missing = blueprint.Machines.Select(vm => vm.Template).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(name => !templates.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))).ToArray();
                if (missing.Length > 0)
                    overview.Text += Environment.NewLine + Text["recipes.Missing"] + " · " + string.Join(", ", missing);
            }
            if (item.PrivateUpdates)
                overview.Text += Environment.NewLine + Text[revisionSource.Items.Count == 0 ? "first.needTemplate" : "hint.updateProxy"];
        };
        Loaded += async (_, _) => await RunAsync(async () =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "examples", "recipes.json");
            choices.ItemsSource = await Task.Run(() => RecipeCatalog.Read(path));
            choices.SelectedIndex = choices.Items.Count > 0 ? 0 : -1;
        });
    }

    private string Describe(LabRecipe item) => $"{item.Author} · {item.License} · v{item.Version} · VMCrate ≥ {item.MinimumVmCrate}\n" +
        Text[item.PrivateUpdates ? "recipes.expectUpdates" : item.Blueprint?.Machines.Any(vm => vm.Segment is not null) == true
            ? "recipes.expectPrivate" : "recipes.expectOffline"] + Environment.NewLine + string.Join(Environment.NewLine, item.Sources);
}
