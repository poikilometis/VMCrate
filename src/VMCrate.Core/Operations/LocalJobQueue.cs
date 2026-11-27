using System.Text.Json;

namespace VMCrate.Core;

public sealed class LocalJobQueue(string workspace)
{
    private readonly string folder = Path.Combine(LocalPaths.FullPath(workspace), "jobs");

    public LocalJob[] Read() => Access(false, items => items.ToArray());

    public LocalJob Enqueue(LocalJobRequest request) => Access(true, items =>
    {
        if (!request.Valid)
            throw new LabException("error.job");
        var existing = items.FirstOrDefault(item => item.Request == request && item.Status is LocalJobStatus.Queued or LocalJobStatus.Running);
        if (existing is not null)
            return existing;
        var completed = items.Where(item => item.Status is LocalJobStatus.Succeeded or LocalJobStatus.Failed or LocalJobStatus.Cancelled)
            .OrderByDescending(item => item.UpdatedUtc).Skip(79).Select(item => item.Id).ToHashSet();
        items.RemoveAll(item => completed.Contains(item.Id));
        if (items.Count >= 256)
            throw new LabException("error.job");
        var job = new LocalJob(Guid.NewGuid(), request, LocalJobStatus.Queued, DateTimeOffset.UtcNow);
        items.Add(job);
        return job;
    });

    public void Cancel(Guid id) => Update(id, job => job.Status switch
    {
        LocalJobStatus.Queued => job with { Status = LocalJobStatus.Cancelled },
        LocalJobStatus.Running => job with { CancelRequested = true },
        _ => job
    });

    public void Retry(Guid id) => Update(id, job => job.Status is LocalJobStatus.Failed or LocalJobStatus.Cancelled
        ? job with { Status = LocalJobStatus.Queued, Failure = null, CancelRequested = false, ElapsedMs = null } : job);

    internal FileStream? TryOwnWorker()
    {
        try { return OpenLock("worker.lock"); }
        catch (IOException error) when (IsLockConflict(error)) { return null; }
    }

    internal void Recover() => Access(true, items =>
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is { Status: LocalJobStatus.Running } job)
                items[i] = job with { Status = job.CancelRequested ? LocalJobStatus.Cancelled : LocalJobStatus.Queued,
                    CancelRequested = false, UpdatedUtc = DateTimeOffset.UtcNow };
        }
        return true;
    });

    internal LocalJob? Claim() => Access(true, items =>
    {
        var index = items.FindIndex(job => job.Status == LocalJobStatus.Queued);
        if (index < 0)
            return null;
        var now = DateTimeOffset.UtcNow;
        return items[index] = items[index] with { Status = LocalJobStatus.Running, Attempts = items[index].Attempts + 1,
            UpdatedUtc = now, StartedUtc = now, ElapsedMs = null, ProcessedBytes = null, TotalBytes = null };
    });

    internal void Progress(Guid id, long bytes, long? total) => Update(id, job => job with { ProcessedBytes = bytes, TotalBytes = total });
    internal void Complete(Guid id, LocalJobStatus status, string? failure = null, long? elapsed = null) => Update(id,
        job => job with { Status = status, Failure = failure, CancelRequested = false, ElapsedMs = elapsed });

    private void Update(Guid id, Func<LocalJob, LocalJob> change) => Access(true, items =>
    {
        var index = items.FindIndex(job => job.Id == id);
        if (index < 0)
            throw new LabException("error.job");
        items[index] = change(items[index]) with { UpdatedUtc = DateTimeOffset.UtcNow };
        return true;
    });

    private T Access<T>(bool write, Func<List<LocalJob>, T> operation)
    {
        using var access = TakeQueueLock();
        var path = Path.Combine(folder, "queue.json");
        LocalPaths.RequireOrdinaryPath(path);
        List<LocalJob> items = [];
        if (File.Exists(path))
        {
            using var input = File.OpenRead(path);
            if (input.Length > 1024 * 1024)
                throw new LabException("error.job");
            try { items = JsonSerializer.Deserialize<List<LocalJob>>(input, AssistantJson.Strict) ?? throw new LabException("error.job"); }
            catch (JsonException error) { throw new LabException("error.job", inner: error); }
            if (items.Count > 256 || items.Any(job => job is null || job.Id == Guid.Empty || job.Request is not { Valid: true } || !Enum.IsDefined(job.Status)) ||
                items.Select(job => job.Id).Distinct().Count() != items.Count)
                throw new LabException("error.job");
        }
        var result = operation(items);
        if (write)
        {
            var temporary = Path.Combine(folder, $"{Guid.NewGuid():N}.tmp");
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(output, items, JsonDefaults.Options);
                    output.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally { File.Delete(temporary); }
        }
        return result;
    }

    private FileStream TakeQueueLock()
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return OpenLock("queue.lock"); }
            catch (IOException error) when (IsLockConflict(error) && attempt < 30) { Thread.Sleep(20); }
        }
    }

    private FileStream OpenLock(string name)
    {
        LocalPaths.MakeDirectory(folder);
        var path = Path.Combine(folder, name);
        LocalPaths.RequireOrdinaryPath(path);
        return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private static bool IsLockConflict(IOException error) => (error.HResult & 0xffff) is 32 or 33 || !OperatingSystem.IsWindows();
}
