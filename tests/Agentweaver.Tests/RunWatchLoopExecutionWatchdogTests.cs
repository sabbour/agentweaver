using FluentAssertions;
using Agentweaver.Api.Runs;

namespace Agentweaver.Tests.Api;

/// <summary>
/// Unit coverage for the watch-loop <see cref="RunWatchLoopService.ExecutionWatchdog"/> — the
/// mechanism that enforces the standing product rule "a run must never die from human-response wait
/// time." The watchdog bounds only spans of ACTIVE workflow execution: while armed it fails a
/// genuinely stuck/runaway active phase, but while a run is parked at a RequestPort awaiting a human
/// decision the wall-clock timeout is suspended, so a human who steps away can never cause the run to
/// be failed. Cancellation from the linked parents (run-cancel / host-shutdown) is unaffected by a
/// pause.
/// </summary>
public sealed class RunWatchLoopExecutionWatchdogTests
{
    [Fact]
    public async Task Armed_ActiveSpanExceedingTimeout_CancelsTheLoop()
    {
        using var cts = new CancellationTokenSource();
        var watchdog = new RunWatchLoopService.ExecutionWatchdog(cts, TimeSpan.FromMilliseconds(150));

        watchdog.Arm();
        watchdog.IsPaused.Should().BeFalse();

        await WaitUntilAsync(() => cts.IsCancellationRequested, TimeSpan.FromSeconds(5));
        cts.IsCancellationRequested.Should().BeTrue(
            "a stuck ACTIVE execution span exceeding the timeout must still be caught");
    }

    [Fact]
    public async Task Paused_DoesNotCancel_EvenLongPastTheActiveTimeout()
    {
        using var cts = new CancellationTokenSource();
        var watchdog = new RunWatchLoopService.ExecutionWatchdog(cts, TimeSpan.FromMilliseconds(100));

        watchdog.Arm();
        watchdog.Pause(); // run parked at a RequestPort awaiting the human
        watchdog.IsPaused.Should().BeTrue();

        await Task.Delay(500); // five active-timeouts' worth of human-wait time
        cts.IsCancellationRequested.Should().BeFalse(
            "human-decision-wait time must never trip the active-execution watchdog");
    }

    [Fact]
    public async Task ReArmedAfterResume_CatchesAStuckSpan_ButNotThePrecedingHumanWait()
    {
        using var cts = new CancellationTokenSource();
        var watchdog = new RunWatchLoopService.ExecutionWatchdog(cts, TimeSpan.FromMilliseconds(150));

        watchdog.Arm();
        watchdog.Pause();       // parked awaiting a human
        await Task.Delay(350);  // a long human wait — must NOT cancel
        cts.IsCancellationRequested.Should().BeFalse("the human-wait span must not count against the deadline");

        watchdog.Arm();         // human responded; active execution resumes and then hangs
        await WaitUntilAsync(() => cts.IsCancellationRequested, TimeSpan.FromSeconds(5));
        cts.IsCancellationRequested.Should().BeTrue(
            "a stuck active span AFTER resume must still be caught by the re-armed watchdog");
    }

    [Fact]
    public void Paused_StillHonorsLinkedParentCancellation()
    {
        using var parent = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(parent.Token);
        var watchdog = new RunWatchLoopService.ExecutionWatchdog(linked, TimeSpan.FromMinutes(5));

        watchdog.Pause(); // wall-clock timeout suspended...

        parent.Cancel();  // ...but run-cancel / host-shutdown must still cancel immediately
        linked.IsCancellationRequested.Should().BeTrue(
            "pausing the timeout must never block run cancellation or host shutdown");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(25);
        }
    }
}
