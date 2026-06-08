// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Configuration;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Aspire.Cli.Telemetry;

// This file is the CLI's OpenTelemetry wiring point. It decides which ActivitySources
// are listened to and where they are exported; the activity creation APIs live in
// AspireCliTelemetry and ProfilingTelemetry.
//
// Keep reported telemetry, profiling telemetry, and debug diagnostics on separate providers.
// Reported telemetry is allowed to leave the machine through Azure Monitor, while
// profiling and diagnostic telemetry are intentionally local and opt-in because they can
// include high-cardinality process, path, and startup timing details.
//
// Enablement is intentionally separate:
// - Reported telemetry is on by default and is disabled with ASPIRE_CLI_TELEMETRY_OPTOUT=true.
// - Profiling telemetry requires ASPIRE_PROFILING_ENABLED=true plus OTEL_EXPORTER_OTLP_ENDPOINT
//   (and typically OTEL_EXPORTER_OTLP_PROTOCOL=grpc). ASPIRE_STARTUP_PROFILING_ENABLED is the
//   legacy alias that remains supported for existing scripts.
// - DEBUG-only diagnostics use ASPIRE_CLI_CONSOLE_EXPORTER_LEVEL=Diagnostic, or OTLP export when
//   OTEL_EXPORTER_OTLP_ENDPOINT is set without profiling enabled.

/// <summary>
/// Manages OpenTelemetry TracerProvider instances for the CLI.
/// Maintains separate providers for reported telemetry, profiling telemetry, and debug diagnostics.
/// </summary>
internal sealed class TelemetryManager : IDisposable
{
    // Remote export connection string for Application Insights. Intentionally hard-coded.
    private const string ApplicationInsightsConnectionString = "InstrumentationKey=e39510fc-95a1-423d-9f33-6121bf0d2113;IngestionEndpoint=https://centralus-2.in.applicationinsights.azure.com/;LiveEndpoint=https://centralus.livediagnostics.monitor.azure.com/;ApplicationId=4d8bb9db-b7ab-49f9-978b-80ae1e83f6da";

#if DEBUG
    // No timeout in debug builds
    private const int ShutDownTimeoutMilliseconds = -1;
#else
    // Chosen to provide time to send remaining telemetry without noticeably delaying exit.
    private const int ShutDownTimeoutMilliseconds = 200;
#endif
    private const int ProfilingForceFlushTimeoutMilliseconds = 5000;

    // The agent telemetry command runs fire-and-forget from an agent hook and exits immediately,
    // so the short Release shutdown flush (200ms) is not enough to reliably export the single
    // just-created span. The command path force-flushes the reported provider with this larger
    // bound before exit so the event is not silently dropped.
    private const int ReportedForceFlushTimeoutMilliseconds = 3000;

    private readonly TracerProvider? _azureMonitorProvider;
    private readonly TracerProvider? _profilingProvider;
    private readonly TracerProvider? _debugDiagnosticProvider;

    private bool _shuttingDown;

    /// <summary>
    /// Initializes a new instance of the <see cref="TelemetryManager"/> class.
    /// </summary>
    /// <param name="configuration">The configuration to read telemetry settings from.</param>
    /// <param name="args">The command-line arguments.</param>
    public TelemetryManager(IConfiguration configuration, string[]? args = null)
    {
        // Don't send telemetry for informational commands or if the user has opted out.
        var hasOptOutArg = args?.Any(a => CommonOptionNames.InformationalOptionNames.Contains(a)) ?? false;

        // The dedicated AI opt-out only suppresses the agent telemetry command path so that
        // setting ASPIRE_CLI_AGENT_TELEMETRY_OPTOUT does not disable general CLI telemetry. It is
        // applied here (before any reported provider is created) so the opt-out reliably prevents
        // the agent_telemetry span from being exported, not just from being populated.
        var agentTelemetryOptOut = AgentTelemetryInvocation.Matches(args) &&
            configuration.GetBool(AspireCliTelemetry.AgentTelemetryOptOutConfigKey, defaultValue: false);

        var telemetryOptOut = hasOptOutArg || agentTelemetryOptOut || configuration.GetBool(AspireCliTelemetry.TelemetryOptOutConfigKey, defaultValue: false);

        var profilingEnabled =
            configuration.GetBool(Aspire.Hosting.KnownConfigNames.ProfilingEnabled) ??
            configuration.GetBool(Aspire.Hosting.KnownConfigNames.Legacy.StartupProfilingEnabled, defaultValue: false);
        var requestedOtlpExporter = !string.IsNullOrEmpty(configuration[AspireCliTelemetry.OtlpExporterEndpointConfigKey]);
        var useProfilingProvider = profilingEnabled && requestedOtlpExporter;

#if DEBUG
        var consoleExporterLevel = configuration.GetEnum<ConsoleExporterLevel>(AspireCliTelemetry.ConsoleExporterLevelConfigKey, defaultValue: null);
        // Preserve the DEBUG-only diagnostic OTLP path for non-profiling diagnostics. When
        // profiling is enabled, the same OTLP endpoint is reserved for the profiling provider
        // so reported/diagnostic sources do not get mixed into startup profiling exports.
        var useDebugDiagnosticOtlpExporter = requestedOtlpExporter && !profilingEnabled;
#else
        ConsoleExporterLevel? consoleExporterLevel = null;
        var useDebugDiagnosticOtlpExporter = false;
#endif
        var useDebugDiagnosticProvider = useDebugDiagnosticOtlpExporter || consoleExporterLevel == ConsoleExporterLevel.Diagnostic;

        // Don't create any providers if nothing is enabled
        if (telemetryOptOut && !useProfilingProvider && !useDebugDiagnosticProvider)
        {
            return;
        }

        var resource = ResourceBuilder.CreateDefault().AddService(
            serviceName: "aspire-cli",
            serviceVersion: VersionHelper.GetDefaultTemplateVersion());

        // Create Azure Monitor provider if connection string is provided.
        // The Azure Monitor only exports telemetry from the Reported activity source.
        if (!telemetryOptOut)
        {
            var azureMonitorBuilder = Sdk.CreateTracerProviderBuilder()
                .AddSource(AspireCliTelemetry.ReportedActivitySourceName)
                .SetResourceBuilder(resource)
                .AddAzureMonitorTraceExporter(o =>
                {
                    o.ConnectionString = ApplicationInsightsConnectionString;
                    o.EnableLiveMetrics = false;
                    o.StorageDirectory = GetTelemetryStoragePath();
                });

#if DEBUG
            if (consoleExporterLevel == ConsoleExporterLevel.Reported)
            {
                azureMonitorBuilder.AddConsoleExporter();
            }
#endif

            _azureMonitorProvider = azureMonitorBuilder.Build();
        }

        if (useProfilingProvider)
        {
            _profilingProvider = Sdk.CreateTracerProviderBuilder()
                .AddSource(ProfilingTelemetry.ActivitySourceName)
                .SetResourceBuilder(resource)
                .AddOtlpExporter()
                .Build();
        }

        if (useDebugDiagnosticProvider)
        {
            var diagnosticBuilder = Sdk.CreateTracerProviderBuilder()
                .AddSource(AspireCliTelemetry.DiagnosticsActivitySourceName)
                .SetResourceBuilder(resource);

            if (consoleExporterLevel == ConsoleExporterLevel.Diagnostic)
            {
                diagnosticBuilder.AddConsoleExporter();
            }

            if (useDebugDiagnosticOtlpExporter)
            {
                diagnosticBuilder.AddOtlpExporter();
            }

            _debugDiagnosticProvider = diagnosticBuilder.Build();
        }
    }

    /// <summary>
    /// Gets whether Azure Monitor telemetry is enabled.
    /// </summary>
    public bool HasAzureMonitor => _azureMonitorProvider is not null;

    /// <summary>
    /// Gets whether profiling telemetry export is enabled.
    /// </summary>
    public bool HasProfilingProvider => _profilingProvider is not null;

    /// <summary>
    /// Gets whether DEBUG-only diagnostic telemetry export is enabled.
    /// </summary>
    public bool HasDiagnosticProvider => _debugDiagnosticProvider is not null;

    /// <summary>
    /// Flushes profiling telemetry without shutting down other telemetry providers.
    /// </summary>
    public Task ForceFlushProfilingAsync()
    {
        // OpenTelemetry's TracerProvider flush API is the synchronous
        // ForceFlush(int timeoutMilliseconds) extension method. It can block until the batch
        // exporter drains or the timeout expires, so keep the CLI profile export path async by
        // running that bounded wait on the thread pool; callers still await this so export does not
        // race ahead of pending spans. Adding cancellation here would either skip the flush before
        // it starts or stop waiting while the synchronous flush keeps running; the provider timeout
        // is the actual bound for this best-effort drain.
        return Task.Run(() =>
        {
            _profilingProvider?.ForceFlush(ProfilingForceFlushTimeoutMilliseconds);
        });
    }

    /// <summary>
    /// Flushes reported telemetry without shutting down other telemetry providers.
    /// </summary>
    /// <remarks>
    /// Used by the <c>aspire agent telemetry</c> command, which is invoked fire-and-forget from an
    /// agent hook and exits immediately. The normal shutdown flush window is too short to reliably
    /// drain a single just-created span, so this bounded flush ensures the event leaves the process.
    /// </remarks>
    public Task ForceFlushReportedAsync()
    {
        // See ForceFlushProfilingAsync for why this runs the synchronous, bounded
        // ForceFlush(int) on the thread pool rather than taking a CancellationToken.
        return Task.Run(() =>
        {
            _azureMonitorProvider?.ForceFlush(ReportedForceFlushTimeoutMilliseconds);
        });
    }

    /// <summary>
    /// Shuts down the telemetry providers, flushing any pending telemetry.
    /// </summary>
    public Task ShutdownAsync()
    {
        _shuttingDown = true;

        return Task.Run(() =>
        {
            _azureMonitorProvider?.Shutdown(ShutDownTimeoutMilliseconds);
            _profilingProvider?.Shutdown(ShutDownTimeoutMilliseconds);
            _debugDiagnosticProvider?.Shutdown(ShutDownTimeoutMilliseconds);
        });
    }

    private static string GetTelemetryStoragePath()
    {
        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(homeDirectory, ".aspire", "cli", "telemetrystorage");
    }

    public void Dispose()
    {
        if (!_shuttingDown)
        {
            // Ensure everything is cleaned up for tests. This covers the situation where the host is disposed without a call to ShutdownAsync.
            // The shutdown timeout is zero so not to wait for telemetry to be flushed. Don't want to delay tests.
            // Dispose isn't used here because it always flushes telemetry and waits for completion.
            _azureMonitorProvider?.Shutdown(0);
            _profilingProvider?.Shutdown(0);
            _debugDiagnosticProvider?.Shutdown(0);
        }
    }
}
