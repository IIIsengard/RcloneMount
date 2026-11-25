using RcloneMount.Models;
using RcloneMount.Services;

namespace RcloneMount.Tests;

public class JobQueueCancelTests
{
    // Cancelling a running job is the user's choice, not a failure: it must end as Cancelled
    // (never Failed, which raises the "Transfer job failed" balloon, nor Completed).
    [Fact]
    public async Task Cancel_RunningJob_EndsCancelled_NotFailed()
    {
        if (RealRclone.Exe is not { } exe) return;
        var src = Directory.CreateTempSubdirectory("rm-src-").FullName;
        File.WriteAllBytes(Path.Combine(src, "big.bin"), new byte[2_000_000]);
        var dst = Path.Combine(Path.GetTempPath(), "rm-dst-" + Guid.NewGuid().ToString("N"));
        var queue = new JobQueueService(new RcloneJobService(exe));
        var run = queue.Enqueue(new JobSpec
        {
            Operation = TransferOperation.Copy,
            SourcePath = src,
            DestPath = dst,
            ExtraArgs = new[] { "--bwlimit", "20k" }, // ~100 s for 2 MB: still running when we cancel
        }, "cancel-test");
        try
        {
            await WaitAsync(() => run.State == JobRunState.Running, 15);
            await Task.Delay(2500); // let rclone start and begin transferring
            queue.Cancel(run.Id);
            await WaitAsync(() => run.IsTerminal, 40);

            Assert.Equal(JobRunState.Cancelled, run.State);
            Assert.Equal("Cancelled", run.Outcome);
        }
        finally
        {
            queue.Cancel(run.Id);
            try { Directory.Delete(src, true); } catch { }
            try { Directory.Delete(dst, true); } catch { }
        }
    }

    private static async Task WaitAsync(Func<bool> cond, int seconds)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!cond() && DateTime.UtcNow < end) await Task.Delay(100);
        Assert.True(cond(), "timed out");
    }
}
