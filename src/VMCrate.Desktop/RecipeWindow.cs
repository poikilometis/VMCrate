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
        var overview = Note("");
        var action = Action(Actions(), "action.apply", () =>
        {
            if (choices.SelectedItem is not LabRecipe recipe)
                return Task.CompletedTask;
            if (recipe.Blueprint is { } blueprint)
                new BlueprintEditorWindow(Text, lab, templates, blueprint) { Owner = this }.ShowDialog();
            else
            {
                var images = new ImageCatalogWindow(Text, lab, recipe.PublisherId) { Owner = this };
                if (images.ShowDialog() == true && images.Selection is { } selection &&
                    new NewTemplateBuildWindow(Text, lab, selection) { Owner = this }.ShowDialog() == true)
                    new TemplateBuildWindow(Text, lab) { Owner = this }.ShowDialog();
            }
            return Task.CompletedTask;
        }, true);
        action.IsEnabled = false;
        choices.SelectionChanged += (_, _) =>
        {
            action.IsEnabled = choices.SelectedItem is LabRecipe;
            if (choices.SelectedItem is not LabRecipe item)
                return;
            overview.Text = $"{item.Author} · {item.License} · v{item.Version} · VMCrate ≥ {item.MinimumVmCrate}\n" +
                string.Join(Environment.NewLine, item.Sources);
            if (item.PrivateUpdates)
                overview.Text += Environment.NewLine + Text["hint.updateProxy"];
        };
        Loaded += async (_, _) => await RunAsync(async () =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "examples", "recipes.json");
            choices.ItemsSource = await Task.Run(() => RecipeCatalog.Read(path));
            choices.SelectedIndex = choices.Items.Count > 0 ? 0 : -1;
        });
    }
}
