using System.Diagnostics;

namespace VMCrate.Core;

public sealed class LocalJobWorker(LabService lab, OfficialImages images, Func<Task<HostReadiness>>? readiness = null)
{
    private readonly LocalJobQueue queue = new(lab.WorkspaceDirectory);

    public async Task RunAsync(CancellationToken cancellation = default)
    {
        using var ownership = queue.TryOwnWorker();
        if (ownership is null)
            return;
        queue.Recover();
        for (var idle = 0; idle < 4 && !cancellation.IsCancellationRequested;)
        {
            if (queue.Claim() is { } job)
            {
                await RunJobAsync(job, cancellation);
                idle = 0;
            }
            else
            {
                idle++;
                await Task.Delay(750, cancellation);
            }
        }
    }

    private async Task RunJobAsync(LocalJob job, CancellationToken cancellation)
    {
        using var work = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using var finished = new CancellationTokenSource();
        var monitor = CheckCancellationAsync(job.Id, work, finished.Token);
        var started = Stopwatch.GetTimestamp();
        long Elapsed() => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        try
        {
            await PerformWhenAvailableAsync(job, work.Token);
            queue.Complete(job.Id, LocalJobStatus.Succeeded, elapsed: Elapsed());
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested && !cancellation.IsCancellationRequested)
        {
            queue.Complete(job.Id, LocalJobStatus.Cancelled, elapsed: Elapsed());
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            queue.Complete(job.Id, LocalJobStatus.Failed, "error.timeout", Elapsed());
        }
        catch (Exception error) when (error is LabException or IOException or UnauthorizedAccessException or HttpRequestException)
        {
            queue.Complete(job.Id, LocalJobStatus.Failed, error is LabException problem ? problem.Key : "error.operation", Elapsed());
        }
        finally
        {
            await finished.CancelAsync();
            await monitor;
        }
    }

    private async Task PerformWhenAvailableAsync(LocalJob job, CancellationToken cancellation)
    {
        for (var retry = 0; ; retry++)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                await PerformAsync(job, cancellation);
                return;
            }
            catch (LabException error) when (error.Key == "error.busy" && retry < 30)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellation);
            }
        }
    }

    private async Task CheckCancellationAsync(Guid id, CancellationTokenSource work, CancellationToken finished)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(finished))
            {
                if (queue.Read().Any(job => job.Id == id && job.CancelRequested))
                {
                    await work.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (finished.IsCancellationRequested) { }
    }

    private async Task PerformAsync(LocalJob job, CancellationToken cancellation)
    {
        var request = job.Request;
        var progress = new InlineProgress<FileProgress>(sample => queue.Progress(job.Id, sample.ProcessedBytes, sample.TotalBytes));
        switch (request.Kind)
        {
            case LocalJobKind.Build:
                if (readiness is not null && request.Stage == BuildStage.Provision && (await readiness()).Capacity is { } capacity)
                    lab.CheckBuildCapacity(request.Resource, capacity);
                await lab.ContinueQueuedBuildAsync(request.Resource, request.Stage!.Value, progress, cancellation);
                break;
            case LocalJobKind.Verify:
                if ((await lab.VerifyTemplateAsync(request.Resource, cancellation, progress)).Matches == false)
                    throw new LabException("error.projectIntegrity");
                break;
            case LocalJobKind.Backup:
                await lab.BackupLabAsync(request.Blueprint!, request.Destination!, cancellation, job.Id, progress);
                break;
            case LocalJobKind.RestoreDrill:
                await lab.TestLabBackupAsync(request.Destination!, cancellation, job.Id);
                break;
            case LocalJobKind.Download:
                var items = await images.ListAsync(request.Publisher!, cancellation);
                var image = items.Images.FirstOrDefault(item => item.Sha256.Equals(request.Digest, StringComparison.OrdinalIgnoreCase))
                    ?? throw new LabException("error.imageSource");
                var download = new InlineProgress<DownloadProgress>(sample => queue.Progress(job.Id, sample.ReceivedBytes, sample.TotalBytes));
                await lab.DownloadInstallerAsync(image, download, cancellation);
                break;
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
