// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli;

/// <summary>
/// Thin facade over a single <see cref="CancellationTokenSource"/> that signals when the
/// CLI's graceful-shutdown window has expired. Owned and timed exclusively by
/// <see cref="ConsoleCancellationManager"/>; consumed by long-running ladders
/// (e.g. <c>AppHostServerSession</c>, <c>ProcessGuestLauncher</c>) that need to know
/// when to stop waiting for a cooperative shutdown and escalate to forceful termination.
/// </summary>
/// <remarks>
/// <para>
/// The service intentionally exposes no timing or budget configuration of its own — CCM
/// is the sole timing authority. Multiple <see cref="Expire"/> calls are safe and
/// idempotent; the token transitions from un-cancelled to cancelled exactly once.
/// </para>
/// <para>
/// Registered as a DI singleton via <c>services.AddSingleton(instance)</c> so the
/// container does not take disposal ownership; the bootstrap path (<c>Program.Main</c>)
/// owns the instance lifetime alongside CCM.
/// </para>
/// </remarks>
internal sealed class GracefulShutdownService : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationToken _token;

    public GracefulShutdownService()
    {
        // Capture the token up front so callers can still observe its (final) state
        // after Dispose, matching the pattern used by ConsoleCancellationManager.
        _token = _cts.Token;
    }

    /// <summary>
    /// Fires when the CLI's graceful-shutdown budget has been exhausted (or when an
    /// external signal has determined that further waiting is no longer useful).
    /// </summary>
    public CancellationToken Token => _token;

    /// <summary>
    /// Signals that the graceful-shutdown window is over. Safe to call multiple times
    /// from any thread; the underlying token transitions to cancelled at most once.
    /// </summary>
    public void Expire()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Expire can race with process shutdown after dispose; swallow rather
            // than propagating so callers (signal handlers, watcher continuations)
            // never have to guard against it.
        }
    }

    public void Dispose() => _cts.Dispose();
}
