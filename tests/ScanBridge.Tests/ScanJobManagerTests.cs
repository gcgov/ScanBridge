using ScanBridge.Scanning;
using Xunit;

namespace ScanBridge.Tests;

public class ScanJobManagerTests
{
    private static readonly ScanRequest Request = new(
        DeviceId: "dev-1", DeviceName: "Test", Driver: "wia", PaperSource: "feeder",
        Dpi: 300, ColorMode: "color", PageSize: "letter", ExcludeBlankPages: false, AutoDeskew: false);

    private static async Task WaitForTerminal(ScanJob job)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!job.IsTerminal)
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "job did not reach a terminal state in time");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task CompletedJobExposesContentBytes()
    {
        using var manager = new ScanJobManager((_, progress, _) =>
        {
            progress.Report(1);
            progress.Report(2);
            return Task.FromResult(new byte[] { 1, 2, 3 });
        });

        var job = manager.TryStart(Request, out var errorCode);
        Assert.Null(errorCode);
        Assert.NotNull(job);

        await WaitForTerminal(job!);
        Assert.Equal(ScanJobStatus.Completed, job!.Status);
        Assert.Equal(new byte[] { 1, 2, 3 }, job.ContentBytes);
        Assert.Null(job.ErrorCode);
    }

    [Fact]
    public async Task SecondStartWhileRunningIsRejectedAsBusy()
    {
        var release = new TaskCompletionSource();
        using var manager = new ScanJobManager(async (_, _, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return [];
        });

        var first = manager.TryStart(Request, out _);
        Assert.NotNull(first);

        var second = manager.TryStart(Request, out var errorCode);
        Assert.Null(second);
        Assert.Equal(ScanErrorCodes.ScannerBusy, errorCode);

        release.SetResult();
        await WaitForTerminal(first!);

        var third = manager.TryStart(Request, out errorCode);
        Assert.NotNull(third);
        Assert.Null(errorCode);
        await WaitForTerminal(third!);
    }

    [Fact]
    public async Task CancelMarksJobCanceled()
    {
        using var manager = new ScanJobManager(async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        });

        var job = manager.TryStart(Request, out _);
        Assert.NotNull(job);

        Assert.True(manager.CancelOrDiscard(job!.Id));
        await WaitForTerminal(job);

        Assert.Equal(ScanJobStatus.Canceled, job.Status);
        Assert.Equal(ScanErrorCodes.Canceled, job.ErrorCode);
    }

    [Fact]
    public async Task ScanBridgeExceptionMapsToItsCode()
    {
        using var manager = new ScanJobManager((_, _, _) =>
            Task.FromException<byte[]>(new ScanBridgeException(ScanErrorCodes.DeviceOffline, "Scanner is off")));

        var job = manager.TryStart(Request, out _);
        await WaitForTerminal(job!);

        Assert.Equal(ScanJobStatus.Failed, job!.Status);
        Assert.Equal(ScanErrorCodes.DeviceOffline, job.ErrorCode);
        Assert.Equal("Scanner is off", job.ErrorMessage);
    }

    [Fact]
    public async Task UnexpectedExceptionMapsToScanFailed()
    {
        using var manager = new ScanJobManager((_, _, _) =>
            Task.FromException<byte[]>(new InvalidOperationException("boom")));

        var job = manager.TryStart(Request, out _);
        await WaitForTerminal(job!);

        Assert.Equal(ScanJobStatus.Failed, job!.Status);
        Assert.Equal(ScanErrorCodes.ScanFailed, job.ErrorCode);
    }

    [Fact]
    public async Task ProgressUpdatesPagesScanned()
    {
        var release = new TaskCompletionSource();
        using var manager = new ScanJobManager(async (_, progress, _) =>
        {
            progress.Report(3);
            await release.Task;
            return [9];
        });

        var job = manager.TryStart(Request, out _);
        Assert.NotNull(job);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        // Progress<T> marshals via the thread pool, so poll briefly.
        while (job!.PagesScanned != 3)
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "progress was not observed in time");
            await Task.Delay(10);
        }

        release.SetResult();
        await WaitForTerminal(job);
        Assert.Equal(ScanJobStatus.Completed, job.Status);
    }

    [Fact]
    public async Task DiscardRemovesFinishedJob()
    {
        using var manager = new ScanJobManager((_, _, _) => Task.FromResult(new byte[] { 1 }));

        var job = manager.TryStart(Request, out _);
        await WaitForTerminal(job!);

        Assert.NotNull(manager.Get(job!.Id));
        Assert.True(manager.CancelOrDiscard(job.Id));
        Assert.Null(manager.Get(job.Id));
    }

    [Fact]
    public void UnknownJobReturnsFalseAndNull()
    {
        using var manager = new ScanJobManager((_, _, _) => Task.FromResult(Array.Empty<byte>()));
        Assert.Null(manager.Get("nope"));
        Assert.False(manager.CancelOrDiscard("nope"));
    }
}
