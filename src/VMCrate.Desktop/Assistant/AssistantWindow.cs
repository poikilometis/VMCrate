using System.Windows;
using System.Windows.Controls;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class AssistantWindow : WorkflowWindow
{
    private readonly AssistantSettings settings = new();
    private readonly TextBlock provider;
    private readonly TextBox answer;
    private readonly Button send;
    private readonly TextBlock? detail;
    private readonly WrapPanel? citations;

    internal AssistantWindow(Labels text, AssistantRequest request, Func<string, string> parse, bool showEvidence = false)
        : base(text, "ai.review", 790, 695)
    {
        provider = Note("");
        RenderProvider();
        Note(Text["ai.previewHint"]);
        Field("ai.payload", new TextBox
        {
            Text = request.Prompt, IsReadOnly = true, AcceptsReturn = true, Height = 190,
            TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FlowDirection = FlowDirection.LeftToRight
        });
        var actions = Actions();
        Action(actions, "tools.ai", () =>
        {
            new AssistantSettingsWindow(Text) { Owner = this }.ShowDialog();
            RenderProvider();
            return Task.CompletedTask;
        });
        send = Action(actions, "ai.send", () => SendAsync(request, parse), true);
        answer = Field("ai.result", new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, Height = 190, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        if (showEvidence)
        {
            citations = Actions();
            detail = Note("");
        }
    }

    private Task SendAsync(AssistantRequest request, Func<string, string> parse) => WithCancellationAsync(async cancellation =>
    {
        answer.Clear();
        var client = AssistantSettings.Client(settings.Read());
        var reply = await Task.Run(() => client.CompleteAsync(request, cancellation), cancellation);
        answer.Text = parse(reply);
        send.IsEnabled = false;
    });

    private void RenderProvider() => provider.Text = Text["ai.provider"] + " · " + AssistantSettings.Name(settings.Read().Provider);

    internal static AssistantExplanation? Explain(Window owner, Labels text, string purpose, IReadOnlyList<AssistantFact> facts)
    {
        var work = new AssistantExplanationWork(purpose, text.Current.Id, facts);
        AssistantExplanation? result = null;
        AssistantWindow? window = null;
        window = new AssistantWindow(text, work.Request(), reply =>
        {
            result = work.Parse(reply);
            window!.ShowEvidence(result, facts);
            return FormatExplanation(text, result);
        }, showEvidence: true) { Owner = owner };
        window.ShowDialog();
        return result;
    }

    private void ShowEvidence(AssistantExplanation explanation, IReadOnlyList<AssistantFact> facts)
    {
        if (citations is null || detail is null)
            return;
        citations.Children.Clear();
        var ids = explanation.Findings.SelectMany(item => item.Evidence).ToHashSet(StringComparer.Ordinal);
        foreach (var fact in facts.Where(item => ids.Contains(item.Id)))
        {
            var button = new Button { Content = fact.Id };
            button.Click += (_, _) => detail.Text = fact.Id + Environment.NewLine + fact.Text;
            citations.Children.Add(button);
        }
    }

    internal static string FormatExplanation(Labels text, AssistantExplanation result)
    {
        var findings = string.Join(Environment.NewLine + Environment.NewLine,
            result.Findings.Select(item => $"{item.Text}  [{string.Join(", ", item.Evidence)}]"));
        return string.IsNullOrWhiteSpace(result.NextCheck) ? findings :
            findings + Environment.NewLine + Environment.NewLine + text["ai.nextCheck"] + Environment.NewLine + result.NextCheck;
    }
}
