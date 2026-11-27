using System.Text;
using System.Text.Json;
using VMCrate.Core;

Console.OutputEncoding = new UTF8Encoding(false);
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) => { args.Cancel = true; cancellation.Cancel(); };

try
{
    switch (args)
    {
        case ["validate", var path]:
            var blueprint = LabBlueprint.Read(path);
            Write(new { blueprint.Id, Resources = OfflinePlanning.Validate(blueprint) });
            return 0;
        case ["plan", var path, var workspace, .. var arguments] when arguments.Length is 0 or 1:
            var pins = arguments.Length == 1 ? OfflinePlanning.ReadLock(arguments[0]) : null;
            var plan = await OfflinePlanning.PlanAsync(Path.GetFullPath(workspace), LabBlueprint.Read(path), pins, cancellation.Token);
            Write(plan);
            return plan.Ready ? 0 : 3;
        case ["lock", var path, var workspace, var output]:
            await OfflinePlanning.SaveLockAsync(output, Path.GetFullPath(workspace), LabBlueprint.Read(path), cancellation.Token);
            return 0;
        case ["validate-recipes", var path]:
            var recipes = RecipeCatalog.Read(path);
            Write(recipes.Select(item => new { item.Id, item.Version, item.MinimumVmCrate, item.License }).ToArray());
            return 0;
        case ["check-library", var directory]:
            var library = await OfflineLibraryManifest.VerifyAsync(directory, cancellation.Token);
            Write(new { library.Version, Files = library.Files.Length, Bytes = library.Files.Sum(item => item.Bytes) });
            return 0;
        case ["check-kit", var path]:
            var kit = await OfflinePlanning.CheckArchiveAsync(path, cancellation.Token);
            Write(kit);
            return kit.SelfContained ? 0 : 3;
        default:
            Console.Error.WriteLine("""
                Usage
                  VMCrate.Cli validate <blueprint.json>
                  VMCrate.Cli plan <blueprint.json> <workspace> [blueprint.lock.json]
                  VMCrate.Cli lock <blueprint.json> <workspace> <blueprint.lock.json>
                  VMCrate.Cli check-kit <project.zip>
                  VMCrate.Cli validate-recipes <recipes.json>
                  VMCrate.Cli check-library <kit-directory>
                """);
            return 2;
    }
}
catch (OperationCanceledException) { return 130; }
catch (Exception error) when (error is LabException or IOException or UnauthorizedAccessException or ArgumentException)
{
    var code = error is LabException problem ? problem.Key : "error.operation";
    Console.Error.WriteLine(code);
    return 1;
}

static void Write<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonDefaults.Options));
