// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Specifies the roles that the current resource should be assigned to the target Azure resource.
/// </summary>
/// <remarks>
/// This annotation is applied to compute resources (e.g., Projects or Containers) that need to interact with Azure resources.
/// </remarks>
public class RoleAssignmentAnnotation : IResourceAnnotation
{
    private readonly Func<AzureProvisioningResource?> _targetResolver;
    private bool _targetResolved;
    private AzureProvisioningResource? _target;

    /// <summary>
    /// Initializes a new instance of the <see cref="RoleAssignmentAnnotation"/> class.
    /// </summary>
    /// <param name="target">The Azure resource that the current resource will interact with.</param>
    /// <param name="roles">The roles that the current resource should be assigned to <paramref name="target"/>.</param>
    public RoleAssignmentAnnotation(AzureProvisioningResource target, IReadOnlySet<RoleDefinition> roles)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(roles);

        _targetResolver = () => target;
        Roles = roles;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RoleAssignmentAnnotation"/> class with a target resolved during Azure preparation.
    /// </summary>
    /// <param name="targetResolver">Resolves the Azure resource that the current resource will interact with, or <see langword="null"/> to skip the role assignment.</param>
    /// <param name="roles">The roles that the current resource should be assigned to the resolved target.</param>
    /// <remarks>
    /// Use this overload when a resource owns role assignments for internal Azure resources whose final target can be
    /// changed by later builder calls.
    /// </remarks>
    public RoleAssignmentAnnotation(Func<AzureProvisioningResource?> targetResolver, IReadOnlySet<RoleDefinition> roles)
    {
        ArgumentNullException.ThrowIfNull(targetResolver);
        ArgumentNullException.ThrowIfNull(roles);

        _targetResolver = targetResolver;
        Roles = roles;
    }

    /// <summary>
    /// The Azure resource that the current resource will interact with.
    /// </summary>
    public AzureProvisioningResource Target =>
        TryGetTarget(out var target)
            ? target
            : throw new InvalidOperationException("The role assignment target could not be resolved.");

    /// <summary>
    /// Gets the set of roles the current resource should be assigned to the target Azure resource.
    /// </summary>
    public IReadOnlySet<RoleDefinition> Roles { get; }

    internal bool TryGetTarget([NotNullWhen(true)] out AzureProvisioningResource? target)
    {
        if (!_targetResolved)
        {
            // Deferred targets can depend on later builder mutations such as WithAzureContainerRegistry.
            // Cache the first non-null value so grouping and de-duplication use a stable target, but
            // do not let an early optional probe permanently suppress a target that resolves later.
            target = _targetResolver();
            if (target is not null)
            {
                _target = target;
                _targetResolved = true;
            }

            return target is not null;
        }

        target = _target!;
        return true;
    }
}
