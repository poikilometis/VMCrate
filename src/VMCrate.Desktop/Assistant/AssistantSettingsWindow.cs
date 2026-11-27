using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VMCrate.Core;
using VMCrate.Windows;

namespace VMCrate.Desktop;

internal sealed class AssistantSettingsWindow : WorkflowWindow
{
    private readonly AssistantSettings settings = new();
    private readonly ComboBox providers;
    private readonly TextBox executable, profile;
    private readonly TextBlock message;

    internal AssistantSettingsWindow(Labels text) : base(text, "tools.ai", 650, 515)
    {
        Note(Text["ai.account"]);
        providers = Field("ai.provider", new ComboBox { DisplayMemberPath = "Name", SelectedValuePath = "Provider" });
        providers.ItemsSource = Enum.GetValues<AssistantProvider>()
            .Select(mode => new ProviderItem(mode, AssistantSettings.Name(mode))).ToArray();
        executable = Field("ai.executable", new TextBox { FlowDirection = FlowDirection.LeftToRight });
        Action(Actions(), "action.browse", () =>
        {
            var file = SelectFile("ai.executable", "CLI (*.exe;*.cmd)|*.exe;*.cmd");
            if (file is not null)
                executable.Text = file;
            return Task.CompletedTask;
        });
        profile = Field("ai.profile", new TextBox { FlowDirection = FlowDirection.LeftToRight });
        var actions = Actions();
        Action(actions, "ai.signIn", OpenSignInAsync);
        Action(actions, "ai.checkConnection", InspectAsync);
        Action(actions, "action.save", () =>
        {
            settings.Save(Connection());
            FinishDialog();
            return Task.CompletedTask;
        }, true);
        message = Note("");
        providers.SelectionChanged += (_, _) => LoadSelection();
        providers.SelectedValue = settings.Read().Provider;
    }

    private Task InspectAsync()
    {
        var client = AssistantSettings.Client(Connection());
        return WithCancellationAsync(async token =>
        {
            var result = await Task.Run(() => client.InspectAsync(token));
            message.Text = Text.Format("ai.connectionStatus", result.Version ?? "—",
                Text[result.Compatible ? "trace.check.Passed" : "trace.check.Failed"],
                Text[result.SubscriptionAuthenticated is null ? "trace.check.Unknown" :
                    result.SubscriptionAuthenticated == true ? "trace.check.Passed" : "trace.check.Failed"]);
        });
    }

    private Task OpenSignInAsync()
    {
        AssistantSettings.Client(Connection()).OpenSignIn();
        message.Text = Text["ai.signInOpened"];
        return Task.CompletedTask;
    }

    private CliConnection Connection() => new((AssistantProvider)providers.SelectedValue,
        string.IsNullOrWhiteSpace(executable.Text) ? null : executable.Text.Trim(),
        profile.IsEnabled && !string.IsNullOrWhiteSpace(profile.Text) ? profile.Text.Trim() : null);

    private void LoadSelection()
    {
        if (providers.SelectedValue is not AssistantProvider mode)
            return;
        var connection = settings.Read(mode);
        executable.Text = connection.Executable ?? "";
        profile.Text = connection.ProfileDirectory ?? "";
        profile.IsEnabled = mode != AssistantProvider.Codex;
        message.Text = "";
    }

    private sealed record ProviderItem(AssistantProvider Provider, string Name);
}
