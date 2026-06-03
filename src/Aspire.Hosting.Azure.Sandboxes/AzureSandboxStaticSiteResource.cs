// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents a JavaScript static site deployed into an Azure Container Apps sandbox.
/// </summary>
[AspireExport(ExposeProperties = true)]
public sealed class AzureSandboxStaticSiteResource : Resource, IResourceWithParent<AzureSandboxGroupResource>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AzureSandboxStaticSiteResource"/> class.
    /// </summary>
    /// <param name="name">The Aspire resource name.</param>
    /// <param name="source">The resource that produces the static files.</param>
    /// <param name="parent">The sandbox group that hosts the sandbox.</param>
    /// <param name="sourceWorkingDirectory">The local working directory of the static-site source.</param>
    /// <param name="outputPath">The local output directory path, relative to the JavaScript resource working directory.</param>
    /// <param name="endpointName">The source endpoint name that supplies the sandbox static server target port.</param>
    /// <param name="disk">The sandbox disk image name.</param>
    /// <param name="build">A value indicating whether the JavaScript install and build scripts run before uploading files.</param>
    /// <param name="autoSuspend">A value indicating whether the sandbox can auto-suspend when idle.</param>
    public AzureSandboxStaticSiteResource(
        string name,
        IResource source,
        AzureSandboxGroupResource parent,
        string sourceWorkingDirectory,
        string outputPath,
        string endpointName,
        string disk,
        bool build,
        bool autoSuspend)
        : base(name)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceWorkingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointName);
        ArgumentException.ThrowIfNullOrWhiteSpace(disk);

        Source = source;
        Parent = parent;
        SourceWorkingDirectory = sourceWorkingDirectory;
        OutputPath = outputPath;
        EndpointName = endpointName;
        Disk = disk;
        Build = build;
        AutoSuspend = autoSuspend;
    }

    /// <summary>
    /// Gets the resource that produces the static files.
    /// </summary>
    public IResource Source { get; }

    /// <inheritdoc/>
    public AzureSandboxGroupResource Parent { get; }

    /// <summary>
    /// Gets the local working directory of the static-site source.
    /// </summary>
    public string SourceWorkingDirectory { get; }

    /// <summary>
    /// Gets the local output directory path, relative to the JavaScript resource working directory.
    /// </summary>
    public string OutputPath { get; }

    /// <summary>
    /// Gets the source endpoint name that supplies the sandbox static server target port.
    /// </summary>
    public string EndpointName { get; }

    /// <summary>
    /// Gets the sandbox disk image name.
    /// </summary>
    public string Disk { get; }

    /// <summary>
    /// Gets a value indicating whether the JavaScript install and build scripts run before uploading files.
    /// </summary>
    public bool Build { get; }

    /// <summary>
    /// Gets a value indicating whether the sandbox can auto-suspend when idle.
    /// </summary>
    public bool AutoSuspend { get; }
}
