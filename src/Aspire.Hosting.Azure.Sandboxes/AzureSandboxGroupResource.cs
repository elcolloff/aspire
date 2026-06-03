// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREAZURE001
#pragma warning disable ASPIRECOMPUTE002

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.Sandboxes.Provisioning;
using Aspire.Hosting.Pipelines;
using Azure.Provisioning.Primitives;
using Azure.Provisioning.Resources;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure Container Apps sandbox group.
/// </summary>
[AspireExport(ExposeProperties = true)]
public sealed class AzureSandboxGroupResource : AzureProvisioningResource, IAzureComputeEnvironmentResource
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AzureSandboxGroupResource"/> class.
    /// </summary>
    /// <param name="name">The Aspire resource name.</param>
    /// <param name="configureInfrastructure">The callback that configures Azure provisioning infrastructure.</param>
    public AzureSandboxGroupResource(string name, Action<AzureResourceInfrastructure> configureInfrastructure)
        : base(name, configureInfrastructure)
    {
        Annotations.Add(new PipelineStepAnnotation(async factoryContext =>
        {
            var model = factoryContext.PipelineContext.Model;
            var steps = new List<PipelineStep>
            {
                new()
                {
                    Name = $"prepare-azure-sandboxes-{Name}",
                    Description = $"Prepares Azure sandbox deployment targets for {Name}.",
                    Action = PrepareDeploymentTargetsAsync,
                    DependsOnSteps = [AzureEnvironmentResource.PrepareResourcesStepName, WellKnownPipelineSteps.ValidateComputeEnvironments],
                    RequiredBySteps = [WellKnownPipelineSteps.BeforeStart]
                }
            };

            foreach (var computeResource in model.GetComputeResources())
            {
                var deploymentTarget = computeResource.GetDeploymentTargetAnnotation(this)?.DeploymentTarget;
                if (deploymentTarget is null ||
                    !deploymentTarget.TryGetAnnotationsOfType<PipelineStepAnnotation>(out var annotations))
                {
                    continue;
                }

                foreach (var annotation in annotations)
                {
                    var childFactoryContext = new PipelineStepFactoryContext
                    {
                        PipelineContext = factoryContext.PipelineContext,
                        Resource = deploymentTarget
                    };

                    var deploymentTargetSteps = await annotation.CreateStepsAsync(childFactoryContext).ConfigureAwait(false);
                    foreach (var step in deploymentTargetSteps)
                    {
                        step.Resource ??= deploymentTarget;
                    }

                    steps.AddRange(deploymentTargetSteps);
                }
            }

            if (IsPrimarySandboxGroup(model, this))
            {
                steps.Add(AzureSandboxContainerDeployment.CreateStaleCleanupPipelineStep(this, AzureSandboxContainerDeployment.GetActiveStateSectionNames(model)));
            }

            return steps;
        }));

        Annotations.Add(new PipelineConfigurationAnnotation(context =>
        {
            foreach (var computeResource in context.Model.GetComputeResources())
            {
                var deploymentTarget = computeResource.GetDeploymentTargetAnnotation(this)?.DeploymentTarget;
                if (deploymentTarget is null ||
                    !deploymentTarget.TryGetAnnotationsOfType<PipelineConfigurationAnnotation>(out var annotations))
                {
                    continue;
                }

                foreach (var annotation in annotations)
                {
                    annotation.Callback(context);
                }
            }

            if (IsPrimarySandboxGroup(context.Model, this))
            {
                AzureSandboxContainerDeployment.ConfigureStaleCleanupDestroyOrdering(context, this);
            }
        }));
    }

    /// <summary>
    /// Gets the Azure resource name output reference.
    /// </summary>
    public BicepOutputReference NameOutputReference => new("name", this);

    /// <summary>
    /// Gets the Azure resource ID output reference.
    /// </summary>
    public BicepOutputReference Id => new("id", this);

    internal ManagedServiceIdentityType ManagedIdentityType { get; set; } = ManagedServiceIdentityType.None;

    internal List<AzureUserAssignedIdentityResource> UserAssignedIdentities { get; } = [];

    internal AzureContainerRegistryResource? DefaultContainerRegistry { get; set; }

    IAzureContainerRegistryResource? IAzureComputeEnvironmentResource.ContainerRegistry => ContainerRegistry;

    /// <summary>
    /// Gets the Azure Container Registry resource used by this sandbox group.
    /// </summary>
    public AzureContainerRegistryResource? ContainerRegistry
    {
        get
        {
            var registry = GetContainerRegistry();
            if (registry is null)
            {
                return null;
            }

            if (registry is not AzureContainerRegistryResource azureRegistry)
            {
                throw new InvalidOperationException(
                    $"The container registry configured for the Azure sandbox group '{Name}' is not an Azure Container Registry. " +
                    $"Only Azure Container Registry resources are supported. Use '.WithAzureContainerRegistry()' to configure an Azure Container Registry.");
            }

            return azureRegistry;
        }
    }

    private async Task PrepareDeploymentTargetsAsync(PipelineStepContext context)
    {
        if (!context.ExecutionContext.IsPublishMode)
        {
            return;
        }

        if (this.HasAnnotationOfType<ContainerRegistryReferenceAnnotation>() &&
            DefaultContainerRegistry is not null)
        {
            context.Model.Resources.Remove(DefaultContainerRegistry);
            DefaultContainerRegistry = null;
        }

        var containerRegistry = ContainerRegistry ??
            throw new InvalidOperationException($"No container registry associated with Azure sandbox group '{Name}'. This should have been added automatically.");

        foreach (var resource in context.Model.GetComputeResources())
        {
            var resourceComputeEnvironment = resource.GetComputeEnvironment();
            if (resourceComputeEnvironment is not null && resourceComputeEnvironment != this)
            {
                continue;
            }

            if (resource.GetDeploymentTargetAnnotation(this) is not null)
            {
                continue;
            }

            var sandboxContainer = new AzureSandboxContainerResource(
                $"{resource.Name}-sandbox-container",
                resource,
                this,
                autoSuspend: false);

            sandboxContainer.Annotations.Add(ManifestPublishingCallbackAnnotation.Ignore);
            sandboxContainer.Annotations.Add(new PipelineStepAnnotation(_ => AzureSandboxContainerDeployment.CreatePipelineSteps(sandboxContainer)));
            sandboxContainer.Annotations.Add(new PipelineConfigurationAnnotation(context =>
            {
                AzureSandboxContainerDeployment.ConfigureDeployOrdering(context, sandboxContainer);
                AzureSandboxContainerDeployment.ConfigureDestroyOrdering(context, sandboxContainer);
            }));

            resource.Annotations.Add(new DeploymentTargetAnnotation(sandboxContainer)
            {
                ContainerRegistry = containerRegistry,
                ComputeEnvironment = this
            });
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc/>
    [Experimental("ASPIRECOMPUTE002", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public ReferenceExpression GetHostAddressExpression(EndpointReference endpointReference)
    {
        ArgumentNullException.ThrowIfNull(endpointReference);

        throw CreateEndpointReferenceNotSupportedException(endpointReference.Resource.Name);
    }

    /// <inheritdoc/>
    [Experimental("ASPIRECOMPUTE002", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public ReferenceExpression GetEndpointPropertyExpression(EndpointReferenceExpression endpointReferenceExpression)
    {
        ArgumentNullException.ThrowIfNull(endpointReferenceExpression);

        throw CreateEndpointReferenceNotSupportedException(endpointReferenceExpression.Endpoint.Resource.Name);
    }

    private NotSupportedException CreateEndpointReferenceNotSupportedException(string resourceName) =>
        new($"Endpoint references for resource '{resourceName}' deployed to Azure sandbox group '{Name}' are not supported because sandbox public URLs are assigned by the ACA data plane during deployment.");

    /// <inheritdoc/>
    public override ProvisionableResource AddAsExistingResource(AzureResourceInfrastructure infra)
    {
        var bicepIdentifier = this.GetBicepIdentifier();
        var existing = infra.GetProvisionableResources()
            .OfType<SandboxGroup>()
            .SingleOrDefault(group => group.BicepIdentifier == bicepIdentifier);

        if (existing is not null)
        {
            return existing;
        }

        var sandboxGroup = SandboxGroup.FromExisting(bicepIdentifier);

        if (!TryApplyExistingResourceAnnotation(this, infra, sandboxGroup))
        {
            sandboxGroup.Name = NameOutputReference.AsProvisioningParameter(infra);
        }

        infra.Add(sandboxGroup);
        return sandboxGroup;
    }

    private IContainerRegistry? GetContainerRegistry()
    {
        if (this.TryGetLastAnnotation<ContainerRegistryReferenceAnnotation>(out var annotation))
        {
            return annotation.Registry;
        }

        return DefaultContainerRegistry;
    }

    private static bool IsPrimarySandboxGroup(DistributedApplicationModel model, AzureSandboxGroupResource resource) =>
        ReferenceEquals(model.Resources.OfType<AzureSandboxGroupResource>().OrderBy(static group => group.Name, StringComparer.Ordinal).FirstOrDefault(), resource);
}
