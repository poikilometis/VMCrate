using System.Text.Json;
using VMCrate.Core;

namespace VMCrate.Host;

internal sealed record JobEntry(string Owner, string RecipeFingerprint, RemoteJob Job);
internal sealed record JobJournal(int Version, JobEntry[] Entries);

internal sealed class JobLedger : IDisposable
{
    private const int Capacity = 4096;
    private readonly FileStream processLock;
    private readonly string path;
    private readonly object sync = new();
    private JobEntry[] entries;

    internal JobLedger(string directory)
    {
        var root = WorkspaceVolume.Prepare(directory);
        path = Path.Combine(root, "queue.json");
        var lockPath = Path.Combine(root, "queue.lock");
        LocalPaths.RequireOrdinaryPath(lockPath);
        processLock = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            entries = Read();
            if (entries.Any(entry => entry.Job.Status == RemoteJobStatus.Running))
                Save(entries.Select(entry => entry.Job.Status == RemoteJobStatus.Running
                    ? Change(entry, RemoteJobStatus.Interrupted) : entry).ToArray());
        }
        catch { processLock.Dispose(); throw; }
    }

    internal RemoteJob[] List(QueueAccount account)
    {
        lock (sync)
            return entries.Where(entry => entry.Owner == account.Id).Reverse().Take(128).Select(entry => entry.Job).ToArray();
    }

    internal RemoteJob Submit(QueueAccount account, RemoteRequest request)
    {
        if (request.Id == Guid.Empty || !Enum.IsDefined(request.Action) ||
            request.Recipe is not { Length: > 0 and <= 64 } || !account.Recipes.TryGetValue(request.Recipe, out var recipe))
            throw Error();

        lock (sync)
        {
            var existing = entries.SingleOrDefault(entry => entry.Owner == account.Id && entry.Job.Id == request.Id);
            if (existing is not null)
            {
                if (existing.Job.Action != request.Action || existing.Job.Recipe != request.Recipe)
                    throw Error();
                return existing.Job;
            }
            CheckOpenJobs(account);
            if (entries.Length >= Capacity)
                throw new LabException("error.remoteQueueFull");

            var now = DateTimeOffset.UtcNow;
            var job = new RemoteJob(request.Id, request.Recipe, request.Action, RemoteJobStatus.Queued, now, now);
            Save([.. entries, new JobEntry(account.Id, recipe.Fingerprint, job)]);
            return job;
        }
    }

    internal RemoteJob Retry(QueueAccount account, Guid id)
    {
        lock (sync)
        {
            var previous = entries.SingleOrDefault(entry => entry.Owner == account.Id && entry.Job.Id == id);
            if (previous is null || !account.Recipes.TryGetValue(previous.Job.Recipe, out var recipe) ||
                recipe.Fingerprint != previous.RecipeFingerprint)
                throw Error();
            if (previous.Job.Status == RemoteJobStatus.Queued)
                return previous.Job;
            if (previous.Job.Status is not (RemoteJobStatus.Interrupted or RemoteJobStatus.Failed))
                throw Error();
            CheckOpenJobs(account);
            var retry = Change(previous, RemoteJobStatus.Queued);
            Save([.. entries.Where(entry => entry != previous), retry]);
            return retry.Job;
        }
    }

    internal JobEntry? Claim()
    {
        lock (sync)
        {
            var next = entries.FirstOrDefault(entry => entry.Job.Status == RemoteJobStatus.Queued);
            if (next is null)
                return null;
            var running = Change(next, RemoteJobStatus.Running);
            Replace(next, running);
            return running;
        }
    }

    internal void Complete(JobEntry running, string? error = null)
    {
        lock (sync)
        {
            if (running.Job.Status != RemoteJobStatus.Running || !entries.Contains(running))
                throw Error();
            var status = error is null ? RemoteJobStatus.Succeeded : RemoteJobStatus.Failed;
            Replace(running, Change(running, status, error));
        }
    }

    private void CheckOpenJobs(QueueAccount account)
    {
        var active = entries.Where(entry => entry.Job.Status is RemoteJobStatus.Queued or RemoteJobStatus.Running).ToArray();
        if (active.Length >= 128 || active.Count(entry => entry.Owner == account.Id) >= 8)
            throw new LabException("error.remoteQueueFull");
    }

    private void Replace(JobEntry previous, JobEntry replacement) =>
        Save(entries.Select(entry => entry == previous ? replacement : entry).ToArray());

    private static JobEntry Change(JobEntry entry, RemoteJobStatus status, string? error = null) => entry with
    {
        Job = entry.Job with { Status = status, Error = error, UpdatedUtc = DateTimeOffset.UtcNow }
    };

    private JobEntry[] Read()
    {
        LocalPaths.RequireOrdinaryPath(path);
        if (!File.Exists(path))
            return [];
        using var input = File.OpenRead(path);
        if (input.Length > 4 * 1024 * 1024)
            throw Error();
        var journal = JsonSerializer.Deserialize<JobJournal>(input, RemoteProtocol.Json);
        if (journal is not { Version: 1, Entries.Length: <= Capacity } ||
            journal.Entries.Any(entry => entry?.Job is null || entry.Job.Id == Guid.Empty ||
                !Enum.IsDefined(entry.Job.Status) || !Enum.IsDefined(entry.Job.Action) ||
                entry.Owner is not { Length: >= 1 and <= 32 } || entry.Job.Recipe is not { Length: >= 1 and <= 64 } ||
                !CertificatePins.IsSha256(entry.RecipeFingerprint)) ||
            journal.Entries.Select(entry => (entry.Owner, entry.Job.Id)).Distinct().Count() != journal.Entries.Length)
            throw Error();
        return journal.Entries;
    }

    private void Save(JobEntry[] replacement)
    {
        LocalPaths.RequireOrdinaryPath(path);
        var staged = Path.Combine(Path.GetDirectoryName(path)!, $".queue-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, new JobJournal(1, replacement), RemoteProtocol.Json);
                output.Flush(flushToDisk: true);
            }
            File.Move(staged, path, overwrite: true);
            entries = replacement;
        }
        finally { File.Delete(staged); }
    }

    public void Dispose() => processLock.Dispose();
    private static LabException Error() => new("error.remoteJob");
}
