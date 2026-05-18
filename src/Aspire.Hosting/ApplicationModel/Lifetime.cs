// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Lifetime modes for resources that can outlive the app host process.
/// </summary>
public enum Lifetime
{
    /// <summary>
    /// Create the resource when the app host process starts and dispose of it when the app host process shuts down.
    /// </summary>
    Session,

    /// <summary>
    /// Attempt to re-use a previously created resource if one exists. Do not destroy the resource on app host process shutdown.
    /// </summary>
    Persistent,
}

/// <summary>
/// Annotation that controls the lifetime of a resource.
/// </summary>
[DebuggerDisplay("Type = {GetType().Name,nq}")]
public sealed class LifetimeAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Gets or sets the lifetime type for the resource.
    /// </summary>
    public required Lifetime Lifetime { get; set; }
}

/// <summary>
/// Annotation that configures a resource to match the lifetime of another resource.
/// </summary>
[DebuggerDisplay("Type = {GetType().Name,nq}, Source = {SourceResource.Name,nq}")]
internal sealed class LifetimeReferenceAnnotation(IResource sourceResource) : IResourceAnnotation
{
    /// <summary>
    /// Gets the resource whose lifetime should be used.
    /// </summary>
    public IResource SourceResource { get; } = sourceResource ?? throw new ArgumentNullException(nameof(sourceResource));
}
