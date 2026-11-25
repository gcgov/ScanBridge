using System.Collections.Concurrent;
using ScanBridge.Localization;

namespace ScanBridge.Scanning;

/// <summary>Executes a scan for a job; injected so the manager is testable without hardware.</summary>
public delegate Task<ScanOutput> ScanExecutor(ScanRequest request, IProgress<int> pageProgress, CancellationToken cancellationToken);

/// <summary>
/// In-memory scan job registry. A physical scanner is exclusive, so at most one job
/// runs at a time; a second start attempt is rejected with <see cref="ScanErrorCodes.ScannerBusy"/>.
/// Terminal jobs are kept for <see cref="_retention"/> so the browser can fetch the result, then pruned.
/// </summary>
public sealed class ScanJobManager : IDisposable
{
    private readonly ScanExecutor _executor;
    private readonly TimeSpan _retention;
    private readonly ConcurrentDictionary<string, ScanJob> _jobs = new();
    private readonly System.Threading.Timer _pruneTimer;
    private ScanJob? _activeJob;
    private readonly Lock _startLock = new();

    public ScanJobManager(ScanExecutor executor, TimeSpan? retention = null)
    {
        _executor = executor;
        _retention = retention ?? TimeSpan.FromMinutes(10);
        _pruneTimer = new System.Threading.Timer(_ => Prune(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>Raised when a job has been accepted and starts running.</summary>
    public event Action<ScanJob>? JobStarted;

    /// <summary>Raised when a job reaches a terminal state (completed, failed or canceled).</summary>
    public event Action<ScanJob>? JobFinished;

    public ScanJob? ActiveJob => _activeJob is { IsTerminal: false } active ? active : null;

    /// <summary>Starts a scan job, or returns null with <paramref name="errorCode"/> set when the scanner is busy.</summary>
    public ScanJob? TryStart(ScanRequest request, out string? errorCode)
    {
        ScanJob job;
        lock (_startLock)
        {
            if (_activeJob is { IsTerminal: false })
            {
                errorCode = ScanErrorCodes.ScannerBusy;
                return null;
            }

            job = new ScanJob();
            _activeJob = job;
            _jobs[job.Id] = job;
        }

        errorCode = null;
        JobStarted?.Invoke(job);

        // Don't let the job inherit the caller's execution context. An HTTP request sets
        // its own UI culture from Accept-Language; without this, the JobFinished handlers
        // (the tray) would run in the web page's language instead of the desktop's.
        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(() => RunAsync(job, request));
        }

        return job;
    }

    public ScanJob? Get(string jobId) => _jobs.TryGetValue(jobId, out var job) ? job : null;

    /// <summary>Cancels a running job, or discards a finished one. Returns false when unknown.</summary>
    public bool CancelOrDiscard(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            return false;
        }

        if (job.IsTerminal)
        {
            _jobs.TryRemove(jobId, out _);
            return true;
        }

        try
        {
            job.Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Job finished between the lookup and the cancel; nothing to do.
        }

        return true;
    }

    private async Task RunAsync(ScanJob job, ScanRequest request)
    {
        var progress = new Progress<int>(pages => job.PagesScanned = pages);
        try
        {
            job.Status = ScanJobStatus.Scanning;
            var output = await _executor(request, progress, job.Cts.Token).ConfigureAwait(false);
            job.Format = output.Format;
            job.Files = output.Files;
            job.Status = ScanJobStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            job.Status = ScanJobStatus.Canceled;
            job.ErrorCode = ScanErrorCodes.Canceled;
            job.ErrorMessage = new LocalizedMessage(nameof(Strings.ScanCanceled));
        }
        catch (ScanBridgeException ex)
        {
            job.Status = ScanJobStatus.Failed;
            job.ErrorCode = ex.Code;
            job.ErrorMessage = ex.Text;
        }
        catch (Exception ex)
        {
            job.Status = ScanJobStatus.Failed;
            job.ErrorCode = ScanErrorCodes.ScanFailed;
            job.ErrorMessage = new LocalizedMessage(nameof(Strings.ScanFailed), ex.Message);
        }
        finally
        {
            job.FinishedUtc = DateTimeOffset.UtcNow;
            job.Cts.Dispose();
            JobFinished?.Invoke(job);
        }
    }

    private void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow - _retention;
        foreach (var (id, job) in _jobs)
        {
            if (job.IsTerminal && job.FinishedUtc is { } finished && finished < cutoff)
            {
                _jobs.TryRemove(id, out _);
            }
        }
    }

    public void Dispose()
    {
        _pruneTimer.Dispose();
        foreach (var job in _jobs.Values)
        {
            if (!job.IsTerminal)
            {
                try
                {
                    job.Cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }
}
