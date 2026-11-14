using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaReports.Storage;
using Xunit;

namespace Jellyfin.Plugin.MediaReports.Tests;

public sealed class WorkQueueTests
{
    [Fact]
    public async Task ABacklog_KeepsAtMostItsCapacity_DroppingTheOldest_WithOneWarning()
    {
        var logger = new ListLogger<WorkQueueTests>();
        var queue = new WorkQueue(1000, "library changes", logger);
        using var started = new ManualResetEventSlim();
        using var slow = new ManualResetEventSlim();
        var ran = 0;
        var last = -1;
        queue.Enqueue(() =>
        {
            started.Set();
            slow.Wait();
        });
        started.Wait();
        for (var i = 0; i < 10_000; i++)
        {
            var n = i;
            queue.Enqueue(() =>
            {
                ran++;
                last = n;
            });
        }

        slow.Set();
        await queue.WhenIdle;

        Assert.Equal(1000, ran);
        Assert.Equal(9_999, last);
        Assert.Equal(9_000, queue.Dropped);
        Assert.Single(logger.Warnings);
        Assert.Empty(logger.Errors);
    }

    [Fact]
    public async Task AnUnavailableDatabase_IsLoggedOnce_UntilItWorksAgain_AndOtherFailuresEachTime()
    {
        var logger = new ListLogger<WorkQueueTests>();
        var queue = new WorkQueue(1000, "playback samples", logger);
        for (var i = 0; i < 50; i++)
        {
            queue.Enqueue(() => throw new StorageUnavailableException());
        }

        await queue.WhenIdle;
        Assert.Single(logger.Errors);

        queue.Enqueue(() => { });
        queue.Enqueue(() => throw new StorageUnavailableException());
        queue.Enqueue(() => throw new InvalidOperationException("bad"));
        queue.Enqueue(() => throw new InvalidOperationException("bad"));
        await queue.WhenIdle;
        Assert.Equal(4, logger.Errors.Count);
    }
}
