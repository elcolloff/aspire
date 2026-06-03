// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREAZURE001
#pragma warning disable ASPIRECOMPUTE002

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.Sandboxes.Provisioning;
using Aspire.Hosting.JavaScript;
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
                steps.Add(AzureSandboxStaticSiteDeployment.CreateStaleCleanupPipelineStep(this, AzureSandboxStaticSiteDeployment.GetActiveStateSectionNames(model)));
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
                AzureSandboxStaticSiteDeployment.ConfigureStaleCleanupDestroyOrdering(context, this);
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

    private async Task PrepareDeploymentTargetsAsync(PipelineStepContext context)
    {
        if (!context.ExecutionContext.IsPublishMode)
        {
            return;
        }

        foreach (var resource in context.Model.GetComputeResources())
        {
            var resourceComputeEnvironment = resource.GetComputeEnvironment();
            if (resourceComputeEnvironment is not null && resourceComputeEnvironment != this)
            {
                continue;
            }

            if (resourceComputeEnvironment is null)
            {
                continue;
            }

            if (resource.GetDeploymentTargetAnnotation(this) is not null)
            {
                continue;
            }

            var staticSite = await TryCreateStaticSiteDeploymentTargetAsync(resource, context.CancellationToken).ConfigureAwait(false);
            if (staticSite is null)
            {
                throw new NotSupportedException($"Resource '{resource.Name}' cannot be deployed to Azure sandbox group '{Name}'. Azure sandbox groups currently support JavaScript resources published with PublishAsStaticWebsite.");
            }

            resource.Annotations.Add(new DeploymentTargetAnnotation(staticSite)
            {
                ComputeEnvironment = this
            });
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task<AzureSandboxStaticSiteResource?> TryCreateStaticSiteDeploymentTargetAsync(IResource resource, CancellationToken cancellationToken)
    {
        if (!resource.TryGetLastAnnotation<JavaScriptPublishModeAnnotation>(out var publishMode) ||
            publishMode.Mode != JavaScriptPublishMode.StaticWebsite)
        {
            return null;
        }

        if (await HasStaticWebsiteApiProxyAsync(resource, cancellationToken).ConfigureAwait(false))
        {
            throw new NotSupportedException($"Resource '{resource.Name}' uses PublishAsStaticWebsite with an API reverse proxy. Azure sandbox groups currently support static file-only JavaScript static websites.");
        }

        if (!resource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out var dockerfileBuild))
        {
            throw new InvalidOperationException($"Resource '{resource.Name}' is configured as a JavaScript static website but does not have Dockerfile build metadata with the source working directory.");
        }

        var outputPath = ResolveStaticSiteOutputPath(resource, publishMode.OutputPath);

        var staticSite = new AzureSandboxStaticSiteResource(
            $"{resource.Name}-sandbox-site",
            resource,
            this,
            dockerfileBuild.ContextPath,
            outputPath,
            endpointName: "http",
            disk: "node-22",
            build: true,
            autoSuspend: false);

        staticSite.Annotations.Add(ManifestPublishingCallbackAnnotation.Ignore);
        staticSite.Annotations.Add(new ContainerFilesDestinationAnnotation
        {
            Source = resource,
            DestinationPath = "/app/wwwroot"
        });
        var deploymentTarget = staticSite;
        staticSite.Annotations.Add(new PipelineStepAnnotation(_ => AzureSandboxStaticSiteDeployment.CreatePipelineSteps(deploymentTarget)));
        staticSite.Annotations.Add(new PipelineConfigurationAnnotation(context => AzureSandboxStaticSiteDeployment.ConfigureDestroyOrdering(context, deploymentTarget)));

        return staticSite;
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

    private static string ResolveStaticSiteOutputPath(IResource resource, string outputPath)
    {
        if (resource.TryGetLastAnnotation<ContainerFilesSourceAnnotation>(out var sourceAnnotation))
        {
            return ToLocalOutputPath(sourceAnnotation.SourcePath);
        }

        return outputPath;
    }

    private static string ToLocalOutputPath(string containerFilesSourcePath)
    {
        var normalizedPath = containerFilesSourcePath.Replace('\\', '/');

        return normalizedPath switch
        {
            "/app" => ".",
            var path when path.StartsWith("/app/", StringComparison.Ordinal) => path["/app/".Length..],
            var path when !path.StartsWith("/", StringComparison.Ordinal) => path,
            _ => throw new InvalidOperationException($"Container files source path '{containerFilesSourcePath}' cannot be mapped to a local JavaScript output directory.")
        };
    }

    private static async Task<bool> HasStaticWebsiteApiProxyAsync(IResource resource, CancellationToken cancellationToken)
    {
        if (!resource.TryGetEnvironmentVariables(out var environmentAnnotations))
        {
            return false;
        }

        var context = new EnvironmentCallbackContext(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish), resource, cancellationToken: cancellationToken);
        foreach (var annotation in environmentAnnotations)
        {
            await annotation.Callback(context).ConfigureAwait(false);
            if (context.EnvironmentVariables.Keys.Any(static key => key.StartsWith("REVERSEPROXY__", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPrimarySandboxGroup(DistributedApplicationModel model, AzureSandboxGroupResource resource) =>
        ReferenceEquals(model.Resources.OfType<AzureSandboxGroupResource>().OrderBy(static group => group.Name, StringComparer.Ordinal).FirstOrDefault(), resource);
}
