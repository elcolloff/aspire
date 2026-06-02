// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli;

/// <summary>
/// Manages Ctrl+C, SIGINT, and SIGTERM signal handling with a shared <see cref="CancellationTokenSource"/>.
/// On the first termination signal it requests cooperative cancellation; after an optional graceful
/// window elapses it expires <see cref="GracefulShutdownService"/> so long-running ladders escalate to
/// forceful termination; after a final drain budget it signals
/// <see cref="ProcessTerminationCompletionSource"/> so <c>Program.Main</c> abandons the handler task and
/// returns the captured exit code.
/// </summary>
/// <remarks>
/// <para>
/// The three-stage signal counter mirrors the same ladder:
/// </para>
/// <list type="number">
///   <item>First signal — primary <see cref="Token"/> cancels and the graceful watcher starts.</item>
///   <item>Second signal — graceful window is collapsed via <see cref="GracefulShutdownService.Expire"/>;
///         ladders see the graceful token fire immediately and escalate.</item>
///   <item>Third (or later) signal — <see cref="ProcessTerminationCompletionSource"/> fires; Main exits NOW.</item>
/// </list>
/// <para>
/// The completion source completing is treated as a strict superset of graceful expiration:
/// when the source completes for any reason (drain timeout, third signal, future external triggers),
/// <see cref="GracefulShutdownService.Expire"/> is invoked synchronously so ladders observing only
/// the graceful token unblock in time to issue a kill before Main abandons them.
/// </para>
/// <para>
/// Disposing this instance unregisters all signal handlers and disposes the internal token source.
/// The <see cref="GracefulShutdownService"/> is owned by the caller (typically <c>Program.Main</c>)
/// and is not disposed here.
/// </para>
/// </remarks>
internal sealed class ConsoleCancellationManager : IDisposable
{
    // Standard Unix exit codes: 128 + signal number (SIGINT=2, SIGTERM=15).
    // SigIntExitCode (130): used when the user presses Ctrl+C (SIGINT) or Ctrl+Break/SIGQUIT.
    // SigTermExitCode (143): used when the process receives SIGTERM (e.g. container stop, ProcessExit).
    private const int SigIntExitCode = 130;
    private const int SigTermExitCode = 143;

    // Sentinel for "no exit code captured yet". int.MinValue is unreachable for any real
    // process exit code we surface and lets us use a single Interlocked.CompareExchange
    // for first-writer-wins semantics without a separate "is set" flag.
    private const int NoExitCodeSentinel = int.MinValue;

    private readonly CancellationTokenSource _cts = new();
    private readonly GracefulShutdownService _gracefulService;
    private readonly TimeSpan _finalDrainBudget;
    private readonly PosixSignalRegistration? _sigIntRegistration;
    private readonly PosixSignalRegistration? _sigTermRegistration;
    private readonly PosixSignalRegistration? _sigQuitRegistration;
    private readonly CancellationToken _token;
    private ILogger _logger;
    private Task<int>? _startedHandler;
    // Gate that ensures the shutdown ladder (CTS cancel + graceful watcher) is started exactly once.
    // CAS-flipped 0→1 by whichever of RequestShutdown or first-time Cancel arrives first; subsequent
    // callers on either path observe the gate already taken and avoid double-starting the watcher.
    // Kept separate from _userSignalCount so internal RequestShutdown does NOT consume a user signal —
    // otherwise the user's first real Ctrl+C after an internal request would be observed as the second
    // signal and collapse the graceful window immediately.
    private int _ladderStarted;
    // Number of real user-initiated termination signals received (Ctrl+C, SIGINT, SIGTERM, SIGQUIT,
    // ProcessExit). Drives the second-signal (Expire) and third-signal (force exit) escalations.
    // Only Cancel touches this — RequestShutdown does not.
    private int _userSignalCount;
    private int _requestedExitCode = NoExitCodeSentinel;
    private TimeSpan _gracefulBudget = TimeSpan.Zero;

    private readonly TaskCompletionSource<int> _processTerminationCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// A completion source that is signaled with a native exit code when the running handler
    /// does not complete within the configured drain budget after a termination signal,
    /// or when a third Ctrl+C arrives.
    /// </summary>
    internal TaskCompletionSource<int> ProcessTerminationCompletionSource => _processTerminationCompletionSource;

    /// <summary>
    /// Sets the handler task that represents the currently executing command. When a termination
    /// signal arrives, the manager will wait for this task to complete within the configured budgets.
    /// </summary>
    internal void SetStartedHandler(Task<int> handler) => Volatile.Write(ref _startedHandler, handler);

    /// <summary>
    /// Sets the logger instance used for diagnostic messages during signal handling.
    /// Call this once the logging infrastructure is available.
    /// </summary>
    internal void SetLogger(ILogger logger) => Volatile.Write(ref _logger, logger);

    public ConsoleCancellationManager(GracefulShutdownService gracefulService, TimeSpan finalDrainBudget)
    {
        ArgumentNullException.ThrowIfNull(gracefulService);

        _gracefulService = gracefulService;
        _finalDrainBudget = finalDrainBudget;
        _logger = NullLogger.Instance;

        // Set to a field so getting the token doesn't error after dispose.
        _token = _cts.Token;

        // Phase 3 → Phase 2 fallthrough. When the termination completion source completes for any reason
        // (drain timeout, third Ctrl+C, future external triggers), any ladder still observing only the
        // graceful token would otherwise sit on a Task.Delay(budget, gracefulService.Token) and miss its
        // last chance to issue a kill before Main abandons it. Cancel synchronously so this fires before
        // continuations of the completion source observe completion. Expire() is idempotent — multiple
        // calls across the watcher (Phase 1 end), the 2nd-signal branch, and this continuation are safe.
        _processTerminationCompletionSource.Task.ContinueWith(
            static (_, state) => ((GracefulShutdownService)state!).Expire(),
            _gracefulService,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        // Prefer PosixSignalRegistration for both SIGINT and SIGTERM as it handles
        // both signals uniformly and allows cancelling SIGTERM (which Console.CancelKeyPress cannot).
        // Despite the name, PosixSignalRegistration is supported on Windows: the runtime maps
        // SIGINT to CTRL_C_EVENT and SIGTERM to CTRL_CLOSE_EVENT/CTRL_SHUTDOWN_EVENT.
        if (!OperatingSystem.IsAndroid()
            && !OperatingSystem.IsIOS()
            && !OperatingSystem.IsTvOS()
            && !OperatingSystem.IsBrowser())
        {
            _sigIntRegistration = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnPosixSignal);
            _sigTermRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnPosixSignal);

            // SIGQUIT maps to CTRL_BREAK_EVENT on Windows. Register it to maintain parity with
            // Console.CancelKeyPress which handled both Ctrl+C and Ctrl+Break.
            // On Linux/macOS, SIGQUIT's default action produces a core dump which is useful for
            // debugging hung processes — don't intercept it there.
            if (OperatingSystem.IsWindows())
            {
                _sigQuitRegistration = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, OnPosixSignal);
            }
        }
        else
        {
            // Fall back to Console.CancelKeyPress on platforms that don't support PosixSignalRegistration.
            Console.CancelKeyPress += OnCancelKeyPress;
        }

        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    public CancellationToken Token => _token;

    /// <summary>
    /// Token that fires when the graceful-shutdown window has been exhausted (graceful budget elapsed,
    /// second termination signal, or process-termination completion). Convenience accessor — callers
    /// that already have a reference to <see cref="GracefulShutdownService"/> can read its
    /// <see cref="GracefulShutdownService.Token"/> directly.
    /// </summary>
    public CancellationToken GracefulShutdownToken => _gracefulService.Token;

    public bool IsCancellationRequested => _cts.IsCancellationRequested;

    /// <summary>
    /// Exit code captured at the moment cancellation was first requested. First writer wins; set by
    /// either an external termination signal (with the OS-conventional signal exit code) or an internal
    /// caller via <see cref="RequestShutdown(int)"/>. Reads return <see langword="null"/> until the first
    /// caller writes.
    /// </summary>
    public int? RequestedExitCode
    {
        get
        {
            var value = Volatile.Read(ref _requestedExitCode);

            return value == NoExitCodeSentinel ? null : value;
        }
    }

    /// <summary>
    /// Sets the graceful-shutdown budget for the currently-executing command. Default is zero, meaning
    /// ladders that consume <see cref="GracefulShutdownToken"/> fall through to escalation immediately
    /// (preserving today's behavior for every command that doesn't opt in). The <c>aspire run</c> handler
    /// calls this with five seconds so DCP and the AppHost get a real cooperative-shutdown window before
    /// escalation.
    /// </summary>
    public void ConfigureForCommand(TimeSpan gracefulBudget)
    {
        if (gracefulBudget < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(gracefulBudget), "Graceful budget cannot be negative.");
        }

        _gracefulBudget = gracefulBudget;
    }

    /// <summary>
    /// Requests cooperative cancellation with the given exit code. Used by internal teardown callers
    /// (guest exited non-zero, backchannel never completed, normal completion) that need to drive the
    /// same shutdown ladder a user signal would, without tampering with private token sources.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="Cancel"/>, this does NOT contribute to the user-signal escalation counter.
    /// Multiple concurrent internal callers (e.g. backchannel fault racing with guest non-zero exit)
    /// would otherwise be treated as "second Ctrl+C" and collapse the graceful window immediately —
    /// defeating the budget. Instead, the first call captures the exit code and fires the primary CT
    /// exactly once; subsequent calls are no-ops (first-writer-wins on the exit code via
    /// <see cref="TrySetRequestedExitCode"/>). A real Ctrl+C from the user still escalates correctly
    /// because <see cref="Cancel"/> drives <c>_userSignalCount</c> independently of <c>_ladderStarted</c>.
    /// </remarks>
    public void RequestShutdown(int exitCode)
    {
        // First-writer-wins on the exit code, regardless of whether we end up firing the ladder.
        // (A late internal request after Ctrl+C already started the ladder still tries to register,
        // but the SIGINT code stays in place.)
        TrySetRequestedExitCode(exitCode);

        // CAS the ladder-started gate 0→1. If a signal handler or prior internal call already started
        // the ladder, no-op. Critically: we DO NOT touch _userSignalCount here, so a subsequent user
        // Ctrl+C is still seen as the first user signal — not the second.
        if (Interlocked.CompareExchange(ref _ladderStarted, 1, 0) != 0)
        {
            return;
        }

        _logger.LogInformation("Internal shutdown requested with exit code {ExitCode}.", exitCode);

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Process is already tearing down; nothing more to do.
            return;
        }

        _ = ExpireGracefulThenFinalDrainAsync(exitCode);
    }

    private void OnPosixSignal(PosixSignalContext context)
    {
        context.Cancel = true;
        var exitCode = context.Signal switch
        {
            PosixSignal.SIGINT => SigIntExitCode,
            PosixSignal.SIGQUIT => SigIntExitCode,
            _ => SigTermExitCode
        };
        Cancel(exitCode);
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        Cancel(SigIntExitCode);
    }

    private void OnProcessExit(object? sender, EventArgs e) => Cancel(SigTermExitCode);

    internal void Cancel(int exitCode)
    {
        // _userSignalCount tracks real user signals only; _ladderStarted is the one-shot gate that
        // tells us whether THIS Cancel is also the call that needs to start the ladder. Keeping the
        // two counters separate is what prevents internal RequestShutdown from consuming a user
        // signal — without that separation, "internal RequestShutdown + first user Ctrl+C" would
        // collapse the graceful window immediately.
        var userSignalCount = Interlocked.Increment(ref _userSignalCount);
        var ladderJustStarted = Interlocked.CompareExchange(ref _ladderStarted, 1, 0) == 0;

        if (ladderJustStarted)
        {
            // First signal AND ladder not already started by an internal RequestShutdown: capture the
            // exit code, request cooperative cancellation, and schedule the graceful-then-drain watcher.
            // The signal handler returns immediately so Program.Main's Task.WhenAny observes handler
            // completion without being blocked by the handler thread.
            TrySetRequestedExitCode(exitCode);
            _logger.LogInformation("Termination signal received, requesting cancellation.");

            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // A signal can race with process shutdown after cancellation resources are disposed.
                return;
            }

            _ = ExpireGracefulThenFinalDrainAsync(exitCode);
            return;
        }

        if (userSignalCount == 1)
        {
            // First user signal but the ladder was already started by an internal RequestShutdown
            // (e.g. normal guest exit, backchannel fault). The user has only pressed Ctrl+C once,
            // so we do NOT escalate — the graceful watcher continues running. Capture the exit
            // code for first-writer-wins (the internal value normally still wins). A second user
            // Ctrl+C below will escalate as expected.
            TrySetRequestedExitCode(exitCode);
            _logger.LogInformation("Termination signal received while shutdown ladder already running.");
        }
        else if (userSignalCount == 2)
        {
            // Second user signal: collapse Phase 1 immediately. Ladders observing the graceful token
            // unblock and escalate to forceful termination; the watcher's Task.Delay(graceful) gets
            // cancelled and moves on to Phase 2 (final drain).
            _logger.LogWarning("Second termination signal received, expiring graceful shutdown window.");
            _gracefulService.Expire();
        }
        else
        {
            // Third (or later) user signal: caller wants out NOW. Skip both graceful and drain budgets.
            _logger.LogWarning("Third termination signal received, forcing immediate exit.");
            _processTerminationCompletionSource.TrySetResult(exitCode);
        }
    }

    private void TrySetRequestedExitCode(int exitCode)
    {
        // Normalize the pathological caller that happens to pass the sentinel value so we never
        // confuse "no exit code captured" with "exit code is int.MinValue".
        if (exitCode == NoExitCodeSentinel)
        {
            exitCode = -1;
        }

        Interlocked.CompareExchange(ref _requestedExitCode, exitCode, NoExitCodeSentinel);
    }

    private async Task ExpireGracefulThenFinalDrainAsync(int forcedTerminationExitCode)
    {
        try
        {
            // When a debugger is attached, don't escalate or force-terminate — the developer needs
            // unlimited time to step through cancellation/cleanup logic. The graceful token therefore
            // never fires under the debugger via this watcher; ladders that observe it sit indefinitely
            // (the right behavior for stepping). A manual second Ctrl+C still works because it calls
            // _gracefulService.Expire() synchronously via the signal counter, bypassing this method.
            if (Debugger.IsAttached)
            {
                return;
            }

            // Phase 1: graceful window. Delay is cancellable via the graceful token so a 2nd Ctrl+C
            // (which calls _gracefulService.Expire from the signal counter) drops us straight into
            // Phase 2 without waiting out the remaining budget. Zero-budget commands skip the delay
            // entirely and fall through to the unconditional Expire() below.
            if (_gracefulBudget > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(_gracefulBudget, _gracefulService.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 2nd Ctrl+C arrived; fall through to Phase 2.
                }
            }

            // Guarantees the graceful token fires before Phase 2 starts, even when the budget was zero
            // (no Phase 1 delay) or when the delay elapsed without a 2nd Ctrl+C. Idempotent.
            _gracefulService.Expire();

            // Phase 2: final drain. Give the handler a chance to finish gracefully within the configured
            // drain budget. Task.WhenAny completes when either the handler or the delay finishes first,
            // without propagating exceptions from the losing task. It's ok that this delay isn't
            // cancellable — the process is ending.
            var startedHandler = Volatile.Read(ref _startedHandler);

            if (startedHandler is not null)
            {
                var drainTask = Task.Delay(_finalDrainBudget);

                if (await Task.WhenAny(startedHandler, drainTask).ConfigureAwait(false) == startedHandler)
                {
                    return;
                }
            }

            _logger.LogWarning("Handler did not complete within {Timeout}s after graceful expiration, forcing termination.", _finalDrainBudget.TotalSeconds);
            _processTerminationCompletionSource.TrySetResult(forcedTerminationExitCode);
        }
        catch (Exception)
        {
            // Any failure in the watcher path should still force termination rather than hang.
            _processTerminationCompletionSource.TrySetResult(forcedTerminationExitCode);
        }
    }

    public void Dispose()
    {
        _sigIntRegistration?.Dispose();
        _sigTermRegistration?.Dispose();
        _sigQuitRegistration?.Dispose();

        Console.CancelKeyPress -= OnCancelKeyPress;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;

        _cts.Dispose();
    }
}

