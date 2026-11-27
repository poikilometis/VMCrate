using System.Windows;
using System.Windows.Controls;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class RemoteQueueWindow : WorkflowWindow
{
    private readonly ComboBox recipes;
    private readonly ComboBox actions;
    private readonly ListBox jobs;
    private readonly Button refresh, submit, retry;
    private RemoteQueueClient? client;
    private RemoteRequest? request;

    internal RemoteQueueWindow(Labels text) : base(text, "remote.title", 820, 740)
    {
        Note(Text["remote.hint"]);
        var address = ConnectionField("remote.address", 256);
        var server = ConnectionField("remote.serverCertificate", 64);
        var identity = ConnectionField("remote.clientCertificate", 64);
        var connection = Actions();
        Action(connection, "remote.connect", async () =>
        {
            client?.Dispose();
            client = null;
            request = null;
            recipes!.ItemsSource = jobs!.ItemsSource = null;
            UpdateActions();
            var connected = new RemoteQueueClient(address.Text, server.Text.Trim(), identity.Text.Trim());
            try
            {
                await RefreshAsync(connected);
                client = connected;
                UpdateActions();
            }
            catch { connected.Dispose(); throw; }
        }, true);
        refresh = Action(connection, "action.refresh", () => client is null ? Task.CompletedTask : RefreshAsync(client));
        recipes = Field("recipes.title", new ComboBox { DisplayMemberPath = "Label" });
        actions = Field("remote.operation", new ComboBox { DisplayMemberPath = "Label" });
        actions.ItemsSource = Enum.GetValues<RemoteLabAction>().Select(operation => new ActionRow(operation, ActionLabel(operation))).ToArray();
        actions.SelectedIndex = 0;
        var controls = Actions();
        submit = Action(controls, "remote.submit", SubmitAsync, true);
        retry = Action(controls, "remote.retry", RetryAsync);
        jobs = Field("remote.jobs", new ListBox { DisplayMemberPath = "Label", Height = 200 });
        recipes.SelectionChanged += (_, _) => UpdateActions();
        jobs.SelectionChanged += (_, _) => UpdateActions();
        Closed += (_, _) => client?.Dispose();
        UpdateActions();
    }

    private TextBox ConnectionField(string key, int length) =>
        Field(key, new TextBox { MaxLength = length, FlowDirection = FlowDirection.LeftToRight });

    private async Task RefreshAsync(RemoteQueueClient connected)
    {
        var available = connected.RecipesAsync();
        var current = connected.JobsAsync();
        await Task.WhenAll(available, current);
        var selected = (recipes.SelectedItem as RecipeRow)?.Recipe.Id;
        recipes.ItemsSource = (await available).Select(recipe => new RecipeRow(recipe,
            $"{recipe.Name} · v{recipe.Version} · {Text.Format("remote.resources", recipe.Resources.Machines, recipe.Resources.MemoryGiB, recipe.Resources.Processors)}")).ToArray();
        recipes.SelectedItem = recipes.Items.Cast<RecipeRow>().FirstOrDefault(item => item.Recipe.Id == selected);
        if (recipes.SelectedIndex < 0)
            recipes.SelectedIndex = 0;
        var receipt = (jobs.SelectedItem as JobRow)?.Job.Id;
        jobs.ItemsSource = (await current).Select(job => new JobRow(job, JobLabel(job))).ToArray();
        jobs.SelectedItem = jobs.Items.Cast<JobRow>().FirstOrDefault(item => item.Job.Id == receipt);
        UpdateActions();
    }

    private async Task SubmitAsync()
    {
        if (client is null || recipes.SelectedItem is not RecipeRow recipe || actions.SelectedItem is not ActionRow action ||
            !Text.Confirm(this, "remote.confirm", action.Label, recipe.Recipe.Name))
            return;
        if (request is null || request.Action != action.Action || request.Recipe != recipe.Recipe.Id)
            request = new RemoteRequest(Guid.NewGuid(), recipe.Recipe.Id, action.Action);
        var job = await client.SubmitAsync(request);
        request = null;
        await RefreshAsync(client);
        jobs.SelectedItem = jobs.Items.Cast<JobRow>().FirstOrDefault(item => item.Job.Id == job.Id);
    }

    private async Task RetryAsync()
    {
        if (client is null || jobs.SelectedItem is not JobRow row || !Text.Confirm(this, "remote.confirm",
            ActionLabel(row.Job.Action), row.Job.Recipe))
            return;
        await client.RetryAsync(row.Job.Id);
        await RefreshAsync(client);
    }

    private void UpdateActions()
    {
        refresh.IsEnabled = client is not null;
        submit.IsEnabled = client is not null && recipes.SelectedItem is RecipeRow;
        retry.IsEnabled = client is not null && jobs.SelectedItem is JobRow
            { Job.Status: RemoteJobStatus.Interrupted or RemoteJobStatus.Failed };
    }

    private string ActionLabel(RemoteLabAction action) => Text[action switch
    {
        RemoteLabAction.Deploy => "remote.Deploy",
        RemoteLabAction.Start => "action.start",
        _ => "action.powerOff"
    }];

    private string JobLabel(RemoteJob job) =>
        $"{job.UpdatedUtc.ToLocalTime():g} · {job.Recipe} · {ActionLabel(job.Action)} · {Text["remote." + job.Status]}" +
        (job.Error is null ? "" : $" · {Text[job.Error]}");

    private sealed record RecipeRow(RemoteRecipe Recipe, string Label);
    private sealed record ActionRow(RemoteLabAction Action, string Label);
    private sealed record JobRow(RemoteJob Job, string Label);
}
