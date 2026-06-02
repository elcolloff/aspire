// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Processes;

/// <summary>
/// Provides child process shutdown primitives shared by CLI process owners.
/// </summary>
internal static class ProcessTerminator
{
    public static async Task<bool> ShutdownAsync(
        Process process,
        bool requestGracefulShutdown,
        bool entireProcessTree,
        ILogger logger,
        string processDescription,
        CancellationToken gracefulShutdownCancellationToken)
    {
        try
        {
            if (process.HasExited)
            {
                logger.LogDebug("{ProcessDescription} process {ProcessId} already exited.", processDescription, process.Id);
                return true;
            }

            if (requestGracefulShutdown)
            {
                logger.LogDebug("Requesting graceful shutdown of {ProcessDescription} process {ProcessId}.", processDescription, process.Id);
                ProcessSignaler.RequestGracefulShutdown(process.Id, expectedStartTime: null, logger);

                try
                {
                    await process.WaitForExitAsync(gracefulShutdownCancellationToken).ConfigureAwait(false);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    if (process.HasExited)
                    {
                        logger.LogDebug("{ProcessDescription} process {ProcessId} exited while graceful shutdown was being cancelled.", processDescription, process.Id);
                        return true;
                    }
                }

                logger.LogWarning(
                    "{ProcessDescription} process {ProcessId} did not stop gracefully before cancellation. Forcing process to terminate.",
                    processDescription,
                    process.Id);
            }

            if (process.HasExited)
            {
                return true;
            }

            logger.LogDebug(
                "Sending kill to {ProcessDescription} process {ProcessId} (entireProcessTree={EntireProcessTree}).",
                processDescription,
                process.Id,
                entireProcessTree);

            process.Kill(entireProcessTree);
            return false;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogDebug(
                ex,
                "{ProcessDescription} process exited before termination could complete (entireProcessTree={EntireProcessTree}).",
                processDescription,
                entireProcessTree);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "Failed to terminate {ProcessDescription} process (entireProcessTree={EntireProcessTree}).",
                processDescription,
                entireProcessTree);
            return false;
        }
    }

    public static async Task<bool> ShutdownAsync(
        int pid,
        DateTimeOffset? expectedStartTime,
        bool requestGracefulShutdown,
        bool entireProcessTree,
        ILogger logger,
        string processDescription,
        CancellationToken gracefulShutdownCancellationToken)
    {
        // Resolve the pid to a live Process handle here so the inner overload can stay agnostic of
        // how it was obtained. The `using` must extend across the await — otherwise the Process is
        // disposed while the inner overload still depends on the handle (e.g. for WaitForExitAsync
        // on the graceful path). Hence the explicit async + await, not a Task-returning passthrough.
        using var process = ProcessSignaler.TryGetRunningProcess(pid, expectedStartTime, logger);
        if (process is null)
        {
            return true;
        }

        return await ShutdownAsync(
            process,
            requestGracefulShutdown,
            entireProcessTree,
            logger,
            processDescription,
            gracefulShutdownCancellationToken).ConfigureAwait(false);
    }
}
