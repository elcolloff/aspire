// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Cli.Processes;

/// <summary>
/// Tiny adapter that wraps a <see cref="Process"/> in an <see cref="IAsyncDisposable"/> so callers
/// of both isolated and non-isolated spawn paths can hold a uniformly non-null disposable
/// lifetime handle and have a single disposal site. Isolated spawns return the
/// <see cref="IsolatedProcess"/> wrapper directly (which already implements
/// <see cref="IAsyncDisposable"/> and owns extra resources like anonymous pipes); non-isolated
/// spawns wrap the bare <see cref="Process"/> through this adapter.
/// </summary>
internal static class ProcessLifetimeAdapter
{
    public static IAsyncDisposable ForProcess(Process process) => new ProcessOnlyLifetime(process);

    private sealed class ProcessOnlyLifetime(Process process) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            try
            {
                process.Dispose();
            }
            catch
            {
                // Best-effort: disposal may race a concurrent kill / handle close. The caller
                // already awaited the shutdown ladder, so by here the process is either exited
                // or being killed; throwing from a disposal path is never useful.
            }

            return ValueTask.CompletedTask;
        }
    }
}
