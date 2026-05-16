// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Commands;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Tests.Commands;

public class AppHostLauncherTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void GetDetachedFailureMessage_ReturnsBuildSpecificMessage_ForBuildFailureExitCode()
    {
        var message = AppHostLauncher.GetDetachedFailureMessage(ExitCodeConstants.FailedToBuildArtifacts);

        Assert.Equal(RunCommandStrings.AppHostFailedToBuild, message);
    }

    [Fact]
    public void GetDetachedFailureMessage_ReturnsExitCodeMessage_ForUnknownExitCode()
    {
        var message = AppHostLauncher.GetDetachedFailureMessage(123);

        Assert.Contains("123", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ExitCodeConstants.Success, true)]
    [InlineData(ExitCodeConstants.FailedToDotnetRunAppHost, false)]
    public void IsSuccessfulDetachedEarlyExit_OnlyTreatsZeroAsSuccess(int exitCode, bool expected)
    {
        var result = AppHostLauncher.IsSuccessfulDetachedEarlyExit(exitCode);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void GenerateChildLogFilePath_UsesDetachChildNamingWithoutProcessId()
    {
        var logsDirectory = Path.Combine(Path.GetTempPath(), "aspire-cli-tests");
        var now = new DateTimeOffset(2026, 02, 12, 18, 00, 00, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);

        var path = AppHostLauncher.GenerateChildLogFilePath(logsDirectory, timeProvider);
        var fileName = Path.GetFileName(path);

        Assert.StartsWith(logsDirectory, path, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("cli_20260212T180000000_detach-child_", fileName, StringComparison.Ordinal);
        Assert.EndsWith(".log", fileName, StringComparison.Ordinal);
        Assert.DoesNotContain($"_{Environment.ProcessId}", fileName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitForAppHostReadyAsync_TreatsUnavailableReadinessAsReady()
    {
        var connection = new TestAppHostAuxiliaryBackchannel();

        var ready = await AppHostLauncher.WaitForAppHostReadyAsync(connection, CancellationToken.None);

        Assert.True(ready);
    }

    [Fact]
    public async Task WaitForAppHostReadyAsync_PropagatesReadinessFailures()
    {
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            SupportsV3 = true,
            WaitForAppHostReadyHandler = _ => throw new IOException("connection lost")
        };

        var exception = await Assert.ThrowsAsync<IOException>(() => AppHostLauncher.WaitForAppHostReadyAsync(connection, CancellationToken.None));
        Assert.Equal("connection lost", exception.Message);
    }

    [Fact]
    public void DetachedChildEnvironmentFilter_PreservesDebugSessionVariables()
    {
        Assert.True(AppHostLauncher.IsExtensionEnvironmentVariable(KnownConfigNames.ExtensionEndpoint));
        Assert.True(AppHostLauncher.IsExtensionEnvironmentVariable(KnownConfigNames.ExtensionDebugSessionId));

        Assert.False(AppHostLauncher.IsExtensionEnvironmentVariable(KnownConfigNames.DebugSessionInfo));
        Assert.False(AppHostLauncher.IsExtensionEnvironmentVariable(KnownConfigNames.DebugSessionRunMode));
        Assert.False(AppHostLauncher.IsExtensionEnvironmentVariable(KnownConfigNames.DebugSessionPort));
        Assert.False(AppHostLauncher.IsExtensionEnvironmentVariable(KnownConfigNames.DebugSessionToken));
        Assert.False(AppHostLauncher.IsExtensionEnvironmentVariable(KnownConfigNames.DebugSessionServerCertificate));
        Assert.False(AppHostLauncher.IsExtensionEnvironmentVariable(KnownConfigNames.DcpInstanceIdPrefix));
    }

    [Fact]
    public void DetachedChildEnvironment_IncludesProfilingTelemetryContext()
    {
        using var listener = CreateActivityListener("test-detached-child-environment");
        using var source = new ActivitySource("test-detached-child-environment");
        using var activity = source.StartActivity("parent");
        Assert.NotNull(activity);
        activity.SetBaggage(ProfilingTelemetry.Baggage.SessionId, "session-1");
        activity.TraceStateString = "state-1";

        var environment = AppHostLauncher.CreateDetachedChildEnvironment(activity);

        Assert.Equal("true", environment[KnownConfigNames.CliRunDetached]);
        Assert.Equal("true", environment[ProfilingTelemetry.EnvironmentVariables.Enabled]);
        Assert.Equal("session-1", environment[ProfilingTelemetry.EnvironmentVariables.SessionId]);
        Assert.Equal("session-1", environment[KnownConfigNames.Legacy.StartupOperationId]);
        Assert.Equal(activity.Id, environment[ProfilingTelemetry.EnvironmentVariables.TraceParent]);
        Assert.Equal("state-1", environment[ProfilingTelemetry.EnvironmentVariables.TraceState]);
    }

    [Fact]
    public void DetachedChildEnvironment_DoesNotIncludeStartupStatusFile()
    {
        var environment = AppHostLauncher.CreateDetachedChildEnvironment(null);

        Assert.False(environment.ContainsKey("ASPIRE_CLI_START_READY_FILE"));
    }

    [Fact]
    public void DetachedChildEnvironment_IncludesProfilingTelemetryContextFromActiveProfilingSpan()
    {
        using var listener = CreateActivityListener(ProfilingTelemetry.ActivitySourceName);
        using var profilingTelemetry = new ProfilingTelemetry(CreateConfiguration(
            (ProfilingTelemetry.EnvironmentVariables.Enabled, "true")));

        using var activity = profilingTelemetry.StartDetachedSpawnChild("aspire", ["run"], childCommand: "run");
        Assert.True(activity.IsRunning);

        var environment = AppHostLauncher.CreateDetachedChildEnvironment(Activity.Current);

        Assert.Equal("true", environment[KnownConfigNames.CliRunDetached]);
        Assert.Equal("true", environment[ProfilingTelemetry.EnvironmentVariables.Enabled]);
        var sessionId = environment[ProfilingTelemetry.EnvironmentVariables.SessionId];
        Assert.False(string.IsNullOrWhiteSpace(sessionId));
        Assert.Equal(sessionId, environment[KnownConfigNames.Legacy.StartupOperationId]);
        Assert.Equal(Activity.Current?.Id, environment[ProfilingTelemetry.EnvironmentVariables.TraceParent]);
    }

    [Fact]
    public void DetachedChildEnvironment_DoesNotEnableProfilingForNonProfilingActivity()
    {
        using var listener = CreateActivityListener("test-detached-child-environment");
        using var source = new ActivitySource("test-detached-child-environment");
        using var activity = source.StartActivity("parent");
        Assert.NotNull(activity);

        var environment = AppHostLauncher.CreateDetachedChildEnvironment(activity);

        Assert.Equal("true", environment[KnownConfigNames.CliRunDetached]);
        Assert.False(environment.ContainsKey(ProfilingTelemetry.EnvironmentVariables.Enabled));
        Assert.False(environment.ContainsKey(ProfilingTelemetry.EnvironmentVariables.SessionId));
        Assert.False(environment.ContainsKey(ProfilingTelemetry.EnvironmentVariables.TraceParent));
        Assert.False(environment.ContainsKey(ProfilingTelemetry.EnvironmentVariables.TraceState));
    }

    [Fact]
    public void DetachedChildEnvironment_AllowsMissingProfilingTelemetryContext()
    {
        var environment = AppHostLauncher.CreateDetachedChildEnvironment(null);

        Assert.Equal("true", environment[KnownConfigNames.CliRunDetached]);
        Assert.False(environment.ContainsKey(ProfilingTelemetry.EnvironmentVariables.Enabled));
        Assert.False(environment.ContainsKey(ProfilingTelemetry.EnvironmentVariables.SessionId));
        Assert.False(environment.ContainsKey(ProfilingTelemetry.EnvironmentVariables.TraceParent));
        Assert.False(environment.ContainsKey(ProfilingTelemetry.EnvironmentVariables.TraceState));
    }

    [Fact]
    public async Task ReadChildLogTail_ReturnsBoundedRelevantNonEmptyTail()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var childLogFile = Path.Combine(workspace.WorkspaceRoot.FullName, "child.log");
        await File.WriteAllLinesAsync(childLogFile, [
            "[2026-05-15 17:07:24.674] [DBUG] [Features] Feature updateNotificationsEnabled = True (default: True)",
            "[2026-05-15 17:07:25.069] [INFO] [Stdout] :gear: Preparing Aspire server...",
            "[2026-05-15 17:07:27.381] [INFO] [Stdout] Connecting to AppHost...",
            "[2026-05-15 17:07:28.618] [DBUG] [GuestAppHostProject] Executing: /opt/homebrew/bin/npm install",
            "[2026-05-15 17:07:29.512] [INFO] [AppHost] up to date, audited 116 packages in 619ms",
            "[2026-05-15 17:07:29.520] [DBUG] [GuestAppHostProject] Executing: /opt/homebrew/bin/npx --no-install tsc --noEmit -p tsconfig.apphost.json",
            "[2026-05-15 17:07:30.501] [INFO] [AppHost] apphost.ts(5,22): error TS1109: Expression expected.",
            "[2026-05-15 17:07:30.521] [FAIL] [GuestAppHostProject] TypeScript (Node.js) apphost exited with code 2",
            "[2026-05-15 17:07:30.522] [FAIL] [GuestAppHostProject] AppHost server process has exited. Unable to connect to backchannel at /tmp/cli.sock",
            "[2026-05-15 17:07:30.528] [FAIL] [AspireCliTelemetry] An unexpected error occurred: The TypeScript (Node.js) apphost failed.",
            "System.InvalidOperationException: The TypeScript (Node.js) apphost failed.",
            "[2026-05-15 17:07:30.534] [INFO] [Stdout] An unexpected error occurred: The TypeScript (Node.js) apphost failed.",
            "[2026-05-15 17:07:30.540] [INFO] [Stdout] See logs at /tmp/child.log"
        ]);

        var lines = AppHostLauncher.ReadChildLogTail(childLogFile, maxLines: 5);

        Assert.Equal([
            "Executing: /opt/homebrew/bin/npm install",
            "up to date, audited 116 packages in 619ms",
            "Executing: /opt/homebrew/bin/npx --no-install tsc --noEmit -p tsconfig.apphost.json",
            "apphost.ts(5,22): error TS1109: Expression expected.",
            "GuestAppHostProject: TypeScript (Node.js) apphost exited with code 2"
        ], lines);
    }

    [Fact]
    public async Task ReadChildLogReplayTail_ReturnsRicherBoundedRelevantTail()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var childLogFile = Path.Combine(workspace.WorkspaceRoot.FullName, "child.log");
        await File.WriteAllLinesAsync(childLogFile, [
            "[2026-05-15 17:07:24.674] [DBUG] [Features] Feature updateNotificationsEnabled = True (default: True)",
            "[2026-05-15 17:07:25.069] [INFO] [Stdout] :gear: Preparing Aspire server...",
            "[2026-05-15 17:07:27.381] [INFO] [Stdout] Connecting to AppHost...",
            "[2026-05-15 17:07:28.618] [DBUG] [GuestAppHostProject] Executing: /opt/homebrew/bin/npm install",
            "[2026-05-15 17:07:29.512] [INFO] [AppHost] up to date, audited 116 packages in 619ms",
            "[2026-05-15 17:07:29.520] [DBUG] [GuestAppHostProject] Executing: /opt/homebrew/bin/npx --no-install tsc --noEmit -p tsconfig.apphost.json",
            "[2026-05-15 17:07:30.501] [INFO] [AppHost] apphost.ts(5,22): error TS1109: Expression expected.",
            "[2026-05-15 17:07:30.521] [FAIL] [GuestAppHostProject] TypeScript (Node.js) apphost exited with code 2",
            "[2026-05-15 17:07:30.522] [FAIL] [GuestAppHostProject] AppHost server process has exited. Unable to connect to backchannel at /tmp/cli.sock",
            "[2026-05-15 17:07:30.528] [FAIL] [AspireCliTelemetry] An unexpected error occurred: The TypeScript (Node.js) apphost failed.",
            "System.InvalidOperationException: The TypeScript (Node.js) apphost failed.",
            "[2026-05-15 17:07:30.534] [INFO] [Stdout] An unexpected error occurred: The TypeScript (Node.js) apphost failed.",
            "[2026-05-15 17:07:30.540] [INFO] [Stdout] See logs at /tmp/child.log"
        ]);

        var entries = AppHostLauncher.ReadChildLogReplayTail(childLogFile, maxLines: 6);

        Assert.Collection(entries,
            entry =>
            {
                Assert.Equal(CliLogFormat.FileLevelTokens.Debug, entry.Level);
                Assert.Equal(CliLogFormat.Categories.GuestAppHostProject, entry.Category);
                Assert.Equal("Executing: /opt/homebrew/bin/npm install", entry.Message);
            },
            entry =>
            {
                Assert.Equal(CliLogFormat.FileLevelTokens.Information, entry.Level);
                Assert.Equal(CliLogFormat.Categories.AppHost, entry.Category);
                Assert.Equal("up to date, audited 116 packages in 619ms", entry.Message);
            },
            entry =>
            {
                Assert.Equal(CliLogFormat.FileLevelTokens.Debug, entry.Level);
                Assert.Equal(CliLogFormat.Categories.GuestAppHostProject, entry.Category);
                Assert.Equal("Executing: /opt/homebrew/bin/npx --no-install tsc --noEmit -p tsconfig.apphost.json", entry.Message);
            },
            entry =>
            {
                Assert.Equal(CliLogFormat.FileLevelTokens.Information, entry.Level);
                Assert.Equal(CliLogFormat.Categories.AppHost, entry.Category);
                Assert.Equal("apphost.ts(5,22): error TS1109: Expression expected.", entry.Message);
            },
            entry =>
            {
                Assert.Equal(CliLogFormat.FileLevelTokens.Error, entry.Level);
                Assert.Equal(CliLogFormat.Categories.GuestAppHostProject, entry.Category);
                Assert.Equal("TypeScript (Node.js) apphost exited with code 2", entry.Message);
            },
            entry =>
            {
                Assert.Equal(CliLogFormat.FileLevelTokens.Error, entry.Level);
                Assert.Equal(CliLogFormat.Categories.GuestAppHostProject, entry.Category);
                Assert.Equal("AppHost server process has exited. Unable to connect to backchannel at /tmp/cli.sock", entry.Message);
            });
    }

    private static ActivityListener CreateActivityListener(string sourceName)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static IConfiguration CreateConfiguration(params (string Key, string? Value)[] values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
