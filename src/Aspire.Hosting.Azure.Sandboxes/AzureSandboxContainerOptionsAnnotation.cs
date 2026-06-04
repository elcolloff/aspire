// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Captures Azure sandbox-specific runtime options on the compute resource being deployed.
/// </summary>
internal sealed class AzureSandboxContainerOptionsAnnotation : IResourceAnnotation
{
    public string? Cpu { get; set; }

    public string? Memory { get; set; }

    public string? Disk { get; set; }

    public bool? AutoSuspendEnabled { get; set; }

    public int? AutoSuspendInterval { get; set; }

    public string? AutoSuspendMode { get; set; }

    public bool? AutoDeleteEnabled { get; set; }

    public int? AutoDeleteIntervalInDays { get; set; }

    public long? AutoDeleteIntervalInSeconds { get; set; }

    public string? AutoDeleteTrigger { get; set; }

    public Dictionary<string, AzureSandboxEndpointOptions> Endpoints { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Captures Azure sandbox-specific endpoint options.
/// </summary>
internal sealed class AzureSandboxEndpointOptions
{
    public bool? Anonymous { get; set; }
}
