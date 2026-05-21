// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Ats;

/// <summary>
/// ATS exports for resource command operations.
/// </summary>
internal static class ResourceCommandExports
{
    /// <summary>
    /// Gets the resource command service from the service provider.
    /// </summary>
    /// <param name="serviceProvider">The service provider handle.</param>
    /// <returns>A resource command service handle.</returns>
    [AspireExport]
    public static ResourceCommandService GetResourceCommandService(this IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        return serviceProvider.GetRequiredService<ResourceCommandService>();
    }

    /// <summary>
    /// Executes a command for the specified resource.
    /// </summary>
    /// <param name="resourceCommandService">The resource command service handle.</param>
    /// <param name="resource">The resource builder or resource id. The resource id can either exactly match the unique id of the resource or the displayed resource name if the resource name doesn't have duplicates.</param>
    /// <param name="commandName">The command name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The command execution result.</returns>
    [AspireExport("executeResourceCommand", MethodName = "executeCommandAsync")]
    public static Task<ExecuteCommandResult> ExecuteCommandAsync(
        this ResourceCommandService resourceCommandService,
        [AspireUnion(typeof(string), typeof(IResourceBuilder<IResource>))] object resource,
        string commandName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resourceCommandService);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);

        if (resource is string stringResourceId && string.IsNullOrWhiteSpace(stringResourceId))
        {
            throw new ArgumentException("Resource id must not be null or whitespace.", nameof(resource));
        }

        return resource switch
        {
            string resourceId => resourceCommandService.ExecuteCommandAsync(resourceId, commandName, cancellationToken),
            IResourceBuilder<IResource> resourceBuilder => resourceCommandService.ExecuteCommandAsync(resourceBuilder.Resource, commandName, cancellationToken),
            _ => throw new ArgumentException("Resource must be a resource builder or resource id.", nameof(resource))
        };
    }
}
