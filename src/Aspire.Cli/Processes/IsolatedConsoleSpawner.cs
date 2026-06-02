// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Cli.Processes;

/// <summary>
/// Shared helper for the isolated-console spawn path: translates a fully-populated
/// <see cref="ProcessStartInfo"/> into the <see cref="IsolatedProcessStartInfo"/> shape
/// <see cref="IsolatedProcess.Start"/> expects, then spawns the child. Centralizes the
/// translation so every caller (AppHost server, guest apphost, future spawners) sees the
/// same env/arg shape and the same fail-fast contract on Windows.
/// </summary>
internal static class IsolatedConsoleSpawner
{
    /// <summary>
    /// Spawns the process described by <paramref name="startInfo"/> into an isolated console
    /// group (new hidden console on Windows; effectively a thin <see cref="Process.Start(ProcessStartInfo)"/>
    /// wrapper on Unix), optionally bound to the supplied Windows kill-on-close job.
    /// </summary>
    /// <remarks>
    /// On Windows, throws <see cref="ArgumentNullException"/> if <paramref name="consoleProcessJob"/>
    /// is <see langword="null"/>. Isolation without the kill-on-close job means the spawned process
    /// can survive a CLI crash as an orphan in its new console group, defeating the entire point
    /// of the safety net the new-console isolation is supposed to enable.
    /// </remarks>
    public static IsolatedProcess StartIsolated(
        ProcessStartInfo startInfo,
        WindowsConsoleProcessJob? consoleProcessJob,
        Action<int, string> standardOutputHandler,
        Action<int, string> standardErrorHandler)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(standardOutputHandler);
        ArgumentNullException.ThrowIfNull(standardErrorHandler);

        if (OperatingSystem.IsWindows() && consoleProcessJob is null)
        {
            throw new ArgumentNullException(
                nameof(consoleProcessJob),
                "consoleProcessJob is required when spawning into an isolated console on Windows so the spawned process is bound to the CLI's kill-on-close job.");
        }

        var isolatedStartInfo = new IsolatedProcessStartInfo
        {
            FileName = startInfo.FileName,
            WorkingDirectory = startInfo.WorkingDirectory,
            JobHandle = OperatingSystem.IsWindows() ? consoleProcessJob?.Handle : null,
        };

        foreach (var arg in startInfo.ArgumentList)
        {
            isolatedStartInfo.ArgumentList.Add(arg);
        }

        // Mirror the ProcessStartInfo.Environment overlays. Touching IsolatedProcessStartInfo's
        // Environment property snapshots the current process env (same semantics as ProcessStartInfo),
        // and we then overlay every key set on the source. We can't just copy over startInfo.Environment
        // wholesale because that would re-overlay the inherited block onto itself; only iterate the
        // entries the caller actually mutated. ProcessStartInfo doesn't expose a "touched" set, so we
        // overlay everything — extra writes of identical values are harmless.
        foreach (var key in startInfo.Environment.Keys)
        {
            isolatedStartInfo.Environment[key] = startInfo.Environment[key];
        }

        return IsolatedProcess.Start(
            isolatedStartInfo,
            (sender, line) => standardOutputHandler(sender.Id, line),
            (sender, line) => standardErrorHandler(sender.Id, line));
    }
}
