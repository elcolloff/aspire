// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text;

namespace Aspire.Cli.Processes;

internal sealed partial class IsolatedProcess
{
    /// <summary>
    /// Unix implementation — a thin <see cref="Process.Start(ProcessStartInfo)"/> wrapper.
    /// SIGTERM / process groups handle cooperative shutdown on Unix, so we do not need
    /// the new-console gymnastics the Windows partial uses. <see cref="IsolatedProcessStartInfo.JobHandle"/>
    /// is ignored on Unix (Unix process-group reparenting + signal delivery cover the
    /// crash-time case that JobHandle exists to address on Windows).
    /// </summary>
    private static IsolatedProcess StartUnix(
        IsolatedProcessStartInfo startInfo,
        Action<IsolatedProcess, string> standardOutputHandler,
        Action<IsolatedProcess, string> standardErrorHandler)
    {
        var psi = new ProcessStartInfo
        {
            FileName = startInfo.FileName,
            WorkingDirectory = startInfo.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            // Pin encodings so the new pump matches the existing ProcessGuestLauncher behavior
            // regardless of the ambient Console.OutputEncoding (e.g. on container hosts that
            // leave it set to ASCII).
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
        };

        foreach (var arg in startInfo.ArgumentList)
        {
            psi.ArgumentList.Add(arg);
        }

        // Only mutate the ProcessStartInfo env block when the caller actually touched
        // IsolatedProcessStartInfo.Environment. Otherwise leave ProcessStartInfo to inherit
        // the parent's env verbatim — saves a snapshot-and-copy round trip for the common
        // case where nothing was customized.
        if (startInfo.HasCustomEnvironment)
        {
            psi.Environment.Clear();
            foreach (var (key, value) in startInfo.Environment)
            {
                // Match ProcessStartInfo.Environment semantics: a null value means "do not
                // set this variable in the child" — we get there by simply not adding it.
                if (value is not null)
                {
                    psi.Environment[key] = value;
                }
            }
        }

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start interactive child process: {startInfo.FileName}");

        return WrapStartedProcess(
            startInfo,
            process,
            process.StandardOutput,
            process.StandardError,
            standardOutputHandler,
            standardErrorHandler,
            extraDispose: null);
    }
}
