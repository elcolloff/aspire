// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests;

public class ConsoleCancellationManagerTests
{
    [Fact]
    public void Constructor_NullGracefulService_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ConsoleCancellationManager(null!, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ConfigureForCommand_NegativeBudget_Throws()
    {
        using var graceful = new GracefulShutdownService();
        using var manager = new ConsoleCancellationManager(graceful, TimeSpan.FromSeconds(5));

        Assert.Throws<ArgumentOutOfRangeException>(() => manager.ConfigureForCommand(TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public void FirstSignal_RequestsCancellation()
    {
        using var graceful = new GracefulShutdownService();
        using var manager = new ConsoleCancellationManager(graceful, TimeSpan.FromSeconds(5));

        Assert.False(manager.IsCancellationRequested);
        Assert.Null(manager.RequestedExitCode);

        manager.Cancel(130);

        Assert.True(manager.IsCancellationRequested);
        Assert.Equal(130, manager.RequestedExitCode);
    }

    [Fact]
    public void FirstSignal_TokenIsCancelled()
    {
        using var graceful = new GracefulShutdownService();
        using var manager = new ConsoleCancellationManager(graceful, TimeSpan.FromSeconds(5));
        var token = manager.Token;

        Assert.False(token.IsCancellationRequested);

        manager.Cancel(130);

        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public async Task FirstSignal_DefaultZeroBudget_ExpiresGracefulImmediately()
    {
        using var graceful = new GracefulShutdownService();
        using var manager = new ConsoleCancellationManager(graceful, TimeSpan.FromSeconds(30));

        // No ConfigureForCommand — graceful budget defaults to zero.
        // Set a handler that never completes so the drain budget governs forced termination.
        manager.SetStartedHandler(new TaskCompletionSource<int>().Task);

        manager.Cancel(130);

        // The graceful token must fire essentially immediately (no Phase 1 delay to wait through).
        await graceful.Token.WaitForCancellationAsync().DefaultTimeout();
        Assert.True(manager.GracefulShutdownToken.IsCancellationRequested);
    }

    [Fact]
    public async Task FirstSignal_NonZeroBudget_DelaysExpireUntilBudgetElapses()
    {
        using var graceful = new GracefulShutdownService();
        using var manager = new ConsoleCancellationManager(graceful, TimeSpan.FromSeconds(30));
        manager.ConfigureForCommand(TimeSpan.FromMilliseconds(200));

        // Set a handler that never completes so we observe the graceful → drain timing.
        manager.SetStartedHandler(new TaskCompletionSource<int>().Task);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        manager.Cancel(130);

        // Right after Cancel, the graceful token should NOT have fired yet (we're in Phase 1).
        await Task.Delay(50);
        Assert.False(graceful.Token.IsCancellationRequested, "Graceful token fired before budget elapsed.");

        // Wait for the budget to elapse.
        await graceful.Token.WaitForCancellationAsync().DefaultTimeout();
        sw.Stop();

        // We allowed 200ms of grace; allow generous slack for CI scheduling but assert we waited at least most of it.
        Assert.True(sw.ElapsedMilliseconds >= 150, $"Graceful token fired after only {sw.ElapsedMilliseconds}ms (expected ~200ms).");
    }

    [Fact]
    public async Task SecondSignal_ExpiresGracefulImmediately()
    {
        using var graceful = new GracefulShutdownService();
        using var manager = new ConsoleCancellationManager(graceful, TimeSpan.FromSeconds(30));
        // Large graceful budget — without a 2nd signal the token would not fire for 30s.
        manager.ConfigureForCommand(TimeSpan.FromSeconds(30));

        // Set a handler that never completes; 2nd signal should ONLY collapse graceful (not Phase 3).
        manager.SetStartedHandler(new TaskCompletionSource<int>().Task);

        manager.Cancel(130);
        Assert.False(graceful.Token.IsCancellationRequested);

        manager.Cancel(130);

        // 2nd signal expires graceful synchronously.
        Assert.True(graceful.Token.IsCancellationRequested);

        // But the completion source should NOT have fired — that's Phase 3, requires a 3rd signal or drain timeout.
        await Task.Delay(100);
        Assert.False(manager.ProcessTerminationCompletionSource.Task.IsCompleted);
    }

    [Fact]
    public async Task ThirdSignal_FiresProcessTerminationImmediately()
    {
        using var graceful = new GracefulShutdownService();
        using var manager = new ConsoleCancellationManager(graceful, TimeSpan.FromSeconds(30));
        // Long graceful budget so the watcher would not naturally complete in the test window.
        manager.ConfigureForCommand(TimeSpan.FromSeconds(30));

        // Handler that never completes so Phase 2's WhenAny doesn't resolve via the handler branch.
        manager.SetStartedHandler(new TaskCompletionSource<int>().Task);

        manager.Cancel(130);
        manager.Cancel(130);
        manager.Cancel(130);

        var exitCode = await manager.ProcessTerminationCompletionSource.Task.DefaultTimeout();
        Assert.Equal(130, exitCode);
    }

    [Fact]
    public async Task FirstSignal_HandlerCompletesWithinDrainBudget_DoesNotForceTermination()
    {
        using var graceful = new GracefulShutdownService();
        using var manager = new ConsoleCancellationManager(graceful, TimeSpan.FromSeconds(5));

        // Set a handler that completes immediately.
        manager.SetStartedHandler(Task.FromResult(0));

        manager.Cancel(130);

        // Give the async watcher time to evaluate.
        await Task.Delay(100);

        // ProcessTerminationCompletionSource should NOT be signaled because the handler completed in time.
        Assert.False(manager.ProcessTerminationCompletionSource.Task.IsCompleted);
    }

    [Fact]
    public async Task FirstSignal_HandlerExceedsDrainBudget_ForcesTermination()
    {
        using var graceful = new GracefulShutdownService();
        using var manager = new ConsoleCancellationManager(graceful, TimeSpan.FromMilliseconds(50));

        // Set a handler that never completes.
        manager.SetStartedHandler(new TaskCompletionSource<int>().Task);

        manager.Cancel(143);

        var exitCode = await manager.ProcessTerminationCompletionSource.Task.DefaultTimeout();
        Assert.Equal(143, exitCode);
    }

    [Fact]
    public async Task FirstSignal_WithNoHandler_ForcesTerminationAfterDrainBudget()
    {
        using var graceful = new GracefulShutdownService();
        using var manager = new ConsoleCancellationManager(graceful, TimeSpan.FromMilliseconds(50));

        // No handler set, watcher still has to wait out the drain budget before forcing termination.
        manager.Cancel(143);

        var exitCode = await manager.ProcessTerminationCompletionSource.Task.DefaultTimeout();
        Assert.Equal(143, exitCode);
    }

    [Fact]
    public async Task GracefulBudgetElapses_ThenDrainBudgetElapses_FiresProcessTermination()
    {
        using var graceful = new GracefulShutdownService();
        using var manager = new ConsoleCancellationManager(graceful, TimeSpan.FromMilliseconds(50));
        manager.ConfigureForCommand(TimeSpan.FromMilliseconds(50));

        manager.SetStartedHandler(new TaskCompletionSource<int>().Task);
        manager.Cancel(130);

        var exitCode = await manager.ProcessTerminationCompletionSource.Task.DefaultTimeout();
        Assert.Equal(130, exitCode);
        Assert.True(graceful.Token.IsCancellationRequested);
    }

    [Fact]
    public void Cancel_IsNonBlocking()
    {
        using var graceful = new GracefulShutdownService();
        using var manager = new ConsoleCancellationManager(graceful, TimeSpan.FromSeconds(30));
        manager.ConfigureForCommand(TimeSpan.FromSeconds(30));

        manager.SetStartedHandler(new TaskCompletionSource<int>().Task);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        manager.Cancel(130);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1000, $"Cancel took {sw.ElapsedMilliseconds}ms, expected < 1000ms");
        Assert.True(manager.IsCancellationRequested);
    }

    [Fact]
    public void RequestShutdown_RequestsCancellation_AndCapturesExitCode()
    {
        using var graceful = new GracefulShutdownService();
        using var manager = new ConsoleCancellationManager(graceful, TimeSpan.FromSeconds(30));

        manager.RequestShutdown(42);

        Assert.True(manager.IsCancellationRequested);
        Assert.Equal(42, manager.RequestedExitCode);
    }

    [Fact]
    public async Task RequestShutdown_RepeatedInternalCalls_DoNotCollapseGracefulWindow()
    {
        using var graceful = new GracefulShutdownService();
        using var manager = new ConsoleCancellationManager(graceful, TimeSpan.FromSeconds(30));
        manager.ConfigureForCommand(TimeSpan.FromSeconds(30));
        manager.SetStartedHandler(new TaskCompletionSource<int>().Task);

        // Two concurrent internal teardown callers (e.g. backchannel-fail + guest-non-zero) must
        // NOT be treated as "second Ctrl+C" — that would collapse the graceful window and defeat
        // the budget. Only a real user signal via Cancel() should escalate.
        manager.RequestShutdown(42);
        manager.RequestShutdown(99);

        Assert.True(manager.IsCancellationRequested);
        Assert.Equal(42, manager.RequestedExitCode);

        // Graceful token should NOT be expired by repeated internal requests — the watcher's
        // Task.Delay(graceful budget) is still arming. Give the scheduler a tick to prove it.
        await Task.Delay(50);
        Assert.False(graceful.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task RequestShutdown_ThenSingleUserCancel_DoesNotCollapseGracefulWindow()
    {
        // Regression: previously RequestShutdown and Cancel shared a single counter, so the user's
        // first Ctrl+C after any internal RequestShutdown was observed as signalCount == 2 and
        // immediately expired the graceful window — defeating the entire ladder during the normal
        // "guest exited cleanly, drive teardown" flow in GuestAppHostProject.RunAsync.
        using var graceful = new GracefulShutdownService();
        using var manager = new ConsoleCancellationManager(graceful, TimeSpan.FromSeconds(30));
        manager.ConfigureForCommand(TimeSpan.FromSeconds(30));
        manager.SetStartedHandler(new TaskCompletionSource<int>().Task);

        // Internal request starts the ladder.
        manager.RequestShutdown(0);
        Assert.True(manager.IsCancellationRequested);

        // The user then hits Ctrl+C ONCE. This is the user's first signal; they must press a second
        // time to expire the graceful window.
        manager.Cancel(130);

        await Task.Delay(50);
        Assert.False(
            graceful.Token.IsCancellationRequested,
            "First user signal after an internal RequestShutdown must not expire the graceful window.");

        // A second user signal SHOULD escalate.
        manager.Cancel(130);
        await Task.Delay(50);
        Assert.True(
            graceful.Token.IsCancellationRequested,
            "Second user signal must expire the graceful window even if an internal RequestShutdown ran first.");
    }

    [Fact]
    public void RequestedExitCode_FirstCallerWins()
    {
        using var graceful = new GracefulShutdownService();
        using var manager = new ConsoleCancellationManager(graceful, TimeSpan.FromSeconds(30));
        manager.ConfigureForCommand(TimeSpan.FromSeconds(30));

        manager.SetStartedHandler(new TaskCompletionSource<int>().Task);

        manager.Cancel(42);
        manager.RequestShutdown(99);
        manager.Cancel(7);

        Assert.Equal(42, manager.RequestedExitCode);
    }

    [Fact]
    public async Task ProcessTermination_FiresGracefulExpiration_LaddersUnblock()
    {
        using var graceful = new GracefulShutdownService();
        using var manager = new ConsoleCancellationManager(graceful, TimeSpan.FromSeconds(30));

        // A ladder observing only the graceful token, awaiting a long delay.
        var ladderTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), graceful.Token);
                return false;
            }
            catch (OperationCanceledException)
            {
                return true;
            }
        });

        // Simulate Main deciding to leave NOW — completion source fires for reasons unrelated to a
        // 2nd Ctrl+C (e.g. external runtime tear-down). The graceful token must fire too so the ladder
        // unblocks in time to escalate before Main abandons it.
        manager.ProcessTerminationCompletionSource.TrySetResult(99);

        var unblocked = await ladderTask.DefaultTimeout();
        Assert.True(unblocked, "Ladder did not observe graceful token cancellation after process termination fired.");
    }

    [Fact]
    public void Dispose_AllowsSubsequentCancelWithoutException()
    {
        using var graceful = new GracefulShutdownService();
        var manager = new ConsoleCancellationManager(graceful, TimeSpan.FromSeconds(5));
        manager.Dispose();

        // Cancel after dispose should not throw (signal can race with shutdown).
        manager.Cancel(130);
    }

    [Fact]
    public void Token_RemainsAccessibleAfterDispose()
    {
        using var graceful = new GracefulShutdownService();
        var manager = new ConsoleCancellationManager(graceful, TimeSpan.FromSeconds(5));
        var token = manager.Token;
        manager.Dispose();

        // Token should still be accessible (stored in field before dispose).
        Assert.False(token.IsCancellationRequested);
    }
}

internal static class CancellationTokenTestExtensions
{
    public static Task WaitForCancellationAsync(this CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = token.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), tcs);
        tcs.Task.ContinueWith(static (_, state) => ((CancellationTokenRegistration)state!).Dispose(), registration, TaskScheduler.Default);
        return tcs.Task;
    }
}
