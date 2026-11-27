using System.IO;
using VMCrate.Core;
using VMCrate.Windows;
using static VMCrate.Tests.Checks;

namespace VMCrate.Tests;

internal static class AssistantChecks
{
    internal static async Task Contracts()
    {
        var diagnostic = DiagnosticFacts.Proxy(null,
            new(ProbeCheck.Passed, ProbeCheck.Failed, ProbeCheck.Failed, ProbeCheck.Passed, 200, 0, 35));
        var work = new AssistantExplanationWork("Explain a test", "ru", diagnostic);
        var request = work.Request();
        Expect(request.Prompt.Contains("ru-RU") && request.Prompt.Contains("CONNECT=200, HTTPS=000"), "Diagnostic context was lost.");
        var result = work.Parse("""{"findings":[{"text":"Gateway unavailable","evidence":["guest-path"]}],"nextCheck":"Check neighbor state"}""");
        Expect(result.Findings.Length == 1, "Expected validated explanation.");
        Expect(work.Parse("```json\n{\"findings\":[{\"text\":\"Gateway unavailable\",\"evidence\":[\"guest-path\"]}],\"nextCheck\":\"\"}\n```").Findings.Length == 1,
            "A standalone fenced JSON reply should be decoded.");
        await ErrorAsync("error.aiResponse", Run(() => work.Parse(
            """{"findings":[{"text":"Invented","evidence":["bogus"]}],"nextCheck":""}""")));
        await ErrorAsync("error.aiResponse", Run(() => work.Parse("""{"findings":[],"findings":[],"nextCheck":""}""")));

        var draft = new DescriptionBlueprint([new(Guid.NewGuid(), "Trusted Windows", GuestFamily.Windows, true)]);
        var blueprint = draft.Parse(
            """{"id":"example","machines":[{"name":"test","template":"T1","memoryGiB":4,"cpuCount":2,"segment":null}]}""");
        Expect(blueprint.Machines.Single().Template == "Trusted Windows", "The model template ID was not resolved locally.");
        await ErrorAsync("error.aiResponse", Run(() => draft.Parse(
            """{"id":"example","machines":[{"name":"test","template":"T1","memoryGiB":4,"cpuCount":2,"segment":null,"network":"external"}]}""")));
        await ErrorAsync("error.blueprint", Run(() => draft.Parse(
            """{"id":"example","machines":[{"name":"test","template":"T2","memoryGiB":4,"cpuCount":2,"segment":null}]}""")));
    }

    internal static async Task CliTransports()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(AppContext.BaseDirectory, "VMCrate.Tests.exe");
        const string message = "[vmcrate fixture stdin] привет";
        var request = new AssistantRequest(message, """{"type":"object"}""");
        var previous = Environment.GetEnvironmentVariable("VMCRATE_CLI_FIXTURE");
        Environment.SetEnvironmentVariable("VMCRATE_CLI_FIXTURE", "1");
        try
        {
            foreach (var provider in Enum.GetValues<AssistantProvider>())
            {
                var client = new SubscriptionCli(new(provider, path), temp.Root);
                Expect(await client.CompleteAsync(request, default) == """{"answered":true}""", $"Wrong {provider} CLI reply.");
            }
        }
        finally { Environment.SetEnvironmentVariable("VMCRATE_CLI_FIXTURE", previous); }
        Expect(!Directory.EnumerateFileSystemEntries(temp.Root).Any(), "A request artifact survived cleanup.");
    }

    internal static int RunCliFixture(string[] args)
    {
        if (args is ["auth", "status"])
        {
            Console.WriteLine("""{"loggedIn":true,"authMethod":"claude.ai"}""");
            return 0;
        }
        if (args is ["login", "status"])
        {
            Console.WriteLine("Logged in using ChatGPT");
            return 0;
        }
        Console.InputEncoding = Console.OutputEncoding = System.Text.Encoding.UTF8;
        var prompt = Console.In.ReadToEnd();
        Expect(prompt == "[vmcrate fixture stdin] привет" && !args.Contains(prompt), "Prompt belongs on stdin.");
        if (args.Contains("--print"))
        {
            Expect(args.Contains("--safe-mode") && args[Array.IndexOf(args, "--tools") + 1] == "", "Claude tool restrictions missing.");
            Console.WriteLine("""{"structured_output":{"answered":true}}""");
        }
        else if (args.Contains("exec"))
        {
            Expect(args.Contains("--ignore-user-config") && args.Contains("features.shell_tool=false"), "Codex isolation flags missing.");
            File.WriteAllText(args[Array.IndexOf(args, "--output-last-message") + 1], """{"answered":true}""");
        }
        else
        {
            var patch = File.ReadAllText(args[Array.IndexOf(args, "--patch") + 1]);
            Expect(patch.Contains("provider: deepseek-account") && patch.Contains("vmcrateTextOnly"), "DeepSeek account or guard missing.");
            Console.Error.WriteLine("VMCRATE_TOOL_GUARD_ACTIVE");
            Console.WriteLine("""{"answered":true}""");
        }
        return 0;
    }

    internal static async Task ArchivedSessions()
    {
        using var temp = new TemporaryDirectory();
        var lab = new LabService(temp.Root, new FakeHypervisor(), new FakeInbox());
        await lab.ImportTemplateAsync("Template", GuestFamily.Linux, false, temp.File("source.vhdx", "base"));
        var template = (await lab.ObserveAsync()).Templates.Single();
        var id = await lab.PrepareCleanSessionAsync(template.Id, new("Test session", 4, 2), [temp.File("do-not-share.txt", "contents")]);
        await lab.StartAsync(id);
        await lab.EndDisposableAsync(id);

        Expect(lab.ReadSessionReportChoices().Single().Archived && (await lab.ObserveAsync()).Machines.Count == 0,
            "The report should outlive the disposable VM.");
        var archived = await lab.BuildReportAsync(id, []);
        Expect(archived.Files.Length == 1 && archived.Events!.Any(item => item.Operation == RecordedOperation.Remove),
            "The archive lost file evidence or the teardown event.");
        var context = string.Join(" ", SessionFacts.Build(archived).Select(item => item.Text));
        Expect(!context.Contains("do-not-share") && !context.Contains(temp.Root), "AI context included a file name or source path.");
    }
}
