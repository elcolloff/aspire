// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Layout;
using Aspire.Cli.Processes;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Projects;

public class ProcessGuestLauncherTests(ITestOutputHelper outputHelper)
{
    private static readonly TimeSpan s_processStartupTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "This verifies the Unix SIGTERM graceful shutdown path.")]
    public async Task LaunchAsync_CancellationUsesUnixGracefulShutdownBeforeForceKill()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var readyFile = Path.Combine(workspace.WorkspaceRoot.FullName, "ready.txt");
        var launcher = new ProcessGuestLauncher(
            "test",
            NullLogger.Instance,
            commandResolver: command => command == "/bin/sh" ? command : null,
            processShutdownService: CreateProcessShutdownService(workspace));
        using var cancellationTokenSource = new CancellationTokenSource();

        var (exitCode, _) = await launcher.LaunchAsync(
            "/bin/sh",
            [
                "-c",
                "trap 'exit 0' TERM; printf ready > \"$1\"; while :; do sleep 0.1; done",
                "ignored",
                readyFile
            ],
            workspace.WorkspaceRoot,
            new Dictionary<string, string>(),
            cancellationTokenSource.Token,
            afterLaunchAsync: async () =>
            {
                await WaitForFileAsync(readyFile);
                await cancellationTokenSource.CancelAsync();
            }).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, exitCode);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Linux | TestPlatforms.OSX | TestPlatforms.FreeBSD, "This verifies Windows CTRL_BREAK_EVENT process-group shutdown.")]
    public async Task LaunchAsync_CancellationUsesWindowsCtrlBreakBeforeForceKill()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var scriptsDirectory = workspace.CreateDirectory("scripts");
        var outputDirectory = workspace.CreateDirectory("output");
        var scriptPath = Path.Combine(scriptsDirectory.FullName, "ctrl-break.ps1");
        var readyFile = Path.Combine(outputDirectory.FullName, "ready.txt");
        var breakFile = Path.Combine(outputDirectory.FullName, "break.txt");
        await File.WriteAllTextAsync(
            scriptPath,
            """
            param(
                [string] $ReadyPath,
                [string] $BreakPath
            )

            $receivedBreak = [System.Threading.ManualResetEventSlim]::new($false)
            [Console]::CancelKeyPress += {
                param($Sender, $EventArgs)
                if ($EventArgs.SpecialKey -eq [ConsoleSpecialKey]::ControlBreak) {
                    $EventArgs.Cancel = $true
                    Set-Content -Path $BreakPath -Value 'break'
                    $receivedBreak.Set()
                }
            }

            Set-Content -Path $ReadyPath -Value 'ready'
            if ($receivedBreak.Wait([TimeSpan]::FromSeconds(30))) {
                exit 0
            }

            exit 2
            """);
        var launcher = new ProcessGuestLauncher(
            "test",
            NullLogger.Instance,
            processShutdownService: CreateProcessShutdownService(workspace));
        using var cancellationTokenSource = new CancellationTokenSource();

        var (exitCode, _) = await launcher.LaunchAsync(
            "powershell.exe",
            ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath, readyFile, breakFile],
            workspace.WorkspaceRoot,
            new Dictionary<string, string>(),
            cancellationTokenSource.Token,
            afterLaunchAsync: async () =>
            {
                await WaitForFileAsync(readyFile);
                await cancellationTokenSource.CancelAsync();
            }).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(breakFile));
    }

    private static ProcessShutdownService CreateProcessShutdownService(TemporaryWorkspace workspace)
    {
        var dcpDirectory = workspace.WorkspaceRoot.CreateSubdirectory("dcp");
        File.WriteAllText(BundleDiscovery.GetDcpExecutablePath(dcpDirectory.FullName), string.Empty);

        return new ProcessShutdownService(
            new FixedLayoutDiscovery(dcpDirectory.FullName),
            new NullBundleService(),
            new LayoutProcessRunner(new TestProcessExecutionFactory()),
            workspace.CreateExecutionContext(),
            NullLogger<ProcessShutdownService>.Instance,
            TimeProvider.System);
    }

    private static async Task WaitForFileAsync(string path)
    {
        using var timeout = new CancellationTokenSource(s_processStartupTimeout);
        while (!File.Exists(path))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        }
    }

    private sealed class FixedLayoutDiscovery(string dcpDirectory) : ILayoutDiscovery
    {
        public LayoutConfiguration? DiscoverLayout(string? projectDirectory = null) => null;

        public string? GetComponentPath(LayoutComponent component, string? projectDirectory = null)
        {
            return component == LayoutComponent.Dcp ? dcpDirectory : null;
        }

        public bool IsBundleModeAvailable(string? projectDirectory = null) => true;
    }
}
