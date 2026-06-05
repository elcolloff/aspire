// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ExtensionsHealthCheckResult = Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult;

namespace Aspire.Hosting.Ats;

/// <summary>
/// ATS exports for custom health-check registration.
/// </summary>
internal static class HealthCheckExports
{
    /// <summary>
    /// Adds a custom health check callback to the distributed-application builder.
    /// </summary>
    /// <param name="builder">The distributed-application builder.</param>
    /// <param name="name">The health check registration name.</param>
    /// <param name="check">The callback that evaluates the health check.</param>
    [AspireExport]
    public static void AddHealthCheck(this IDistributedApplicationBuilder builder, string name, Func<Task<HealthCheckResult>> check)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(check);

        builder.Services.AddHealthChecks().AddAsyncCheck(name, async () =>
        {
            var result = await check().ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Custom health check '{name}' returned a null result.");
            return result.ToExtensionsHealthCheckResult(name);
        });
    }
}

/// <summary>
/// ATS-friendly custom health check result.
/// </summary>
[AspireDto]
internal sealed class HealthCheckResult
{
    // JSON callbacks can return DTOs with omitted properties. Track the setter
    // separately so an explicit Unhealthy value is not confused with a missing status.
    private bool _hasStatus;
    private HealthStatus _status;

    /// <summary>
    /// Gets the health status returned by the health check.
    /// </summary>
    public HealthStatus Status
    {
        get => _status;
        init
        {
            _status = value;
            _hasStatus = true;
        }
    }

    /// <summary>
    /// Gets an optional description for the health check result.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets optional string data for the health check result.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Data { get; init; }

    internal ExtensionsHealthCheckResult ToExtensionsHealthCheckResult(string name)
    {
        if (!_hasStatus)
        {
            throw new InvalidOperationException($"Custom health check '{name}' returned a result without a status.");
        }

        var data = Data?.ToDictionary(
            pair => pair.Key,
            pair => (object)pair.Value,
            StringComparer.Ordinal);

        return new ExtensionsHealthCheckResult(Status, Description, exception: null, data: data);
    }
}
