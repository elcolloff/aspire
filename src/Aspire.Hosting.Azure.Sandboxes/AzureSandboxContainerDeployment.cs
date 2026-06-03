// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES002
#pragma warning disable ASPIREPIPELINES004
#pragma warning disable ASPIREAZURE001
#pragma warning disable ASPIRECONTAINERRUNTIME001

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Azure;

internal static class AzureSandboxContainerDeployment
{
    private const string SandboxStateParentSection = "Azure:Sandboxes";
    internal const string SandboxStateSectionPrefix = $"{SandboxStateParentSection}:";
    private const string SandboxGroupDataOwnerRole = "Container Apps SandboxGroup Data Owner";
    private const int PublicEndpointTimeoutSeconds = 60;

    public static IEnumerable<PipelineStep> CreatePipelineSteps(AzureSandboxContainerResource resource)
    {
        var deployStepName = GetDeployStepName(resource);
        var destroyStepName = GetDestroyStepName(resource);

        return
        [
            new PipelineStep
            {
                Name = deployStepName,
                Description = $"Deploys compute resource '{resource.TargetResource.Name}' to ACA sandbox '{resource.Name}'.",
                Action = context => DeployAsync(context, resource),
                DependsOnSteps = [AzureEnvironmentResource.ProvisionInfrastructureStepName, WellKnownPipelineSteps.DeployPrereq],
                RequiredBySteps = [WellKnownPipelineSteps.Deploy],
                Tags = [WellKnownPipelineTags.DeployCompute],
                Resource = resource
            },
            new PipelineStep
            {
                Name = destroyStepName,
                Description = $"Deletes ACA sandbox deployment '{resource.Name}'.",
                Action = context => DestroyAsync(context, resource),
                DependsOnSteps = [WellKnownPipelineSteps.DestroyPrereq],
                RequiredBySteps = [WellKnownPipelineSteps.Destroy],
                Resource = resource
            }
        ];
    }

    internal static PipelineStep CreateStaleCleanupPipelineStep(AzureSandboxGroupResource resource, IReadOnlySet<string> activeStateSectionNames)
    {
        return new PipelineStep
        {
            Name = GetStaleCleanupStepName(resource),
            Description = $"Deletes stale ACA sandbox deployments for Azure sandbox group '{resource.Name}'.",
            Action = context => DestroyStaleDeploymentsAsync(context, activeStateSectionNames),
            DependsOnSteps = [WellKnownPipelineSteps.DestroyPrereq],
            RequiredBySteps = [WellKnownPipelineSteps.Destroy],
            Resource = resource
        };
    }

    internal static IReadOnlySet<string> GetActiveStateSectionNames(DistributedApplicationModel model)
    {
        var activeStateSectionNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var resource in model.GetComputeResources())
        {
            if (!resource.TryGetAnnotationsOfType<DeploymentTargetAnnotation>(out var deploymentTargetAnnotations))
            {
                continue;
            }

            foreach (var deploymentTargetAnnotation in deploymentTargetAnnotations)
            {
                if (deploymentTargetAnnotation.DeploymentTarget is AzureSandboxContainerResource sandboxContainer)
                {
                    activeStateSectionNames.Add(GetStateSectionName(sandboxContainer));
                }
            }
        }

        return activeStateSectionNames;
    }

    internal static void ConfigureStaleCleanupDestroyOrdering(PipelineConfigurationContext context, AzureSandboxGroupResource resource)
    {
        var cleanupStepName = GetStaleCleanupStepName(resource);

        foreach (var step in context.Steps.Where(static step => step.Name.StartsWith("destroy-azure-", StringComparison.Ordinal)))
        {
            step.DependsOn(cleanupStepName);
        }
    }

    public static void ConfigureDestroyOrdering(PipelineConfigurationContext context, AzureSandboxContainerResource resource)
    {
        var destroyStepName = GetDestroyStepName(resource);

        foreach (var step in context.Steps.Where(static step => step.Name.StartsWith("destroy-azure-", StringComparison.Ordinal)))
        {
            step.DependsOn(destroyStepName);
        }
    }

    public static void ConfigureDeployOrdering(PipelineConfigurationContext context, AzureSandboxContainerResource resource)
    {
        var pushSteps = context.GetSteps(resource.TargetResource, WellKnownPipelineTags.PushContainerImage);
        var deploySteps = context.GetSteps(resource, WellKnownPipelineTags.DeployCompute);

        deploySteps.DependsOn(pushSteps);
    }

    private static async Task DeployAsync(PipelineStepContext context, AzureSandboxContainerResource resource)
    {
        var targetResource = resource.TargetResource;
        var endpoints = ResolveSandboxEndpoints(resource);
        var deploymentStateManager = context.Services.GetRequiredService<IDeploymentStateManager>();
        var azureState = await GetAzureStateAsync(deploymentStateManager, context.CancellationToken).ConfigureAwait(false);

        var sandboxGroupName = GetRequiredOutput(resource.Parent, "name");
        var command = new AcaCommandContext(azureState.SubscriptionId, azureState.ResourceGroup, azureState.Location, sandboxGroupName);

        var stateSection = await deploymentStateManager.AcquireSectionAsync(GetStateSectionName(resource), context.CancellationToken).ConfigureAwait(false);
        await EnsureSandboxGroupDataOwnerAsync(context, command).ConfigureAwait(false);

        await DeleteExistingDeploymentAsync(context, command, stateSection, throwOnError: false).ConfigureAwait(false);
        await deploymentStateManager.DeleteSectionAsync(stateSection, context.CancellationToken).ConfigureAwait(false);
        stateSection = await deploymentStateManager.AcquireSectionAsync(GetStateSectionName(resource), context.CancellationToken).ConfigureAwait(false);

        var deployId = Guid.NewGuid().ToString("N");
        var diskImageId = string.Empty;
        var sandboxId = string.Empty;
        var addedPorts = new List<SandboxEndpoint>();

        try
        {
            var imageReference = await ResolveContainerImageAsync(context, resource).ConfigureAwait(false);
            var imageMetadata = await ResolveContainerImageMetadataAsync(context, targetResource, imageReference).ConfigureAwait(false);
            var diskImageName = CreateSandboxResourceName(targetResource.Name, deployId);

            var diskTask = await context.ReportingStep.CreateTaskAsync($"Creating sandbox disk image for {targetResource.Name}", context.CancellationToken).ConfigureAwait(false);
            await using (diskTask.ConfigureAwait(false))
            {
                var diskOutput = await CreateDiskImageAsync(context, command, resource, imageReference, diskImageName, deployId).ConfigureAwait(false);
                diskImageId = TryGetJsonString(diskOutput, "id") ?? throw new InvalidOperationException("The aca CLI disk create response did not contain an id.");
                await diskTask.CompleteAsync($"Created sandbox disk image {diskImageId}", CompletionState.Completed, context.CancellationToken).ConfigureAwait(false);
            }

            var environmentVariables = new Dictionary<string, string>(imageMetadata.EnvironmentVariables, StringComparer.Ordinal);
            foreach (var (key, value) in await ResolveEnvironmentVariablesAsync(context, targetResource).ConfigureAwait(false))
            {
                environmentVariables[key] = value;
            }

            var createTask = await context.ReportingStep.CreateTaskAsync($"Creating sandbox for {targetResource.Name}", context.CancellationToken).ConfigureAwait(false);
            await using (createTask.ConfigureAwait(false))
            {
                var createOutput = await CreateSandboxAsync(context, command, resource, diskImageId, environmentVariables, imageMetadata, deployId).ConfigureAwait(false);
                sandboxId = TryGetSandboxId(createOutput) ?? await FindSandboxByLabelAsync(context, command, deployId).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(sandboxId))
                {
                    throw new InvalidOperationException("The aca CLI did not return a sandbox ID and the created sandbox could not be found by label.");
                }

                await createTask.CompleteAsync($"Created sandbox {sandboxId}", CompletionState.Completed, context.CancellationToken).ConfigureAwait(false);
            }

            if (!resource.AutoSuspend)
            {
                var lifecycleTask = await context.ReportingStep.CreateTaskAsync($"Configuring lifecycle for {resource.Name}", context.CancellationToken).ConfigureAwait(false);
                await using (lifecycleTask.ConfigureAwait(false))
                {
                    await RunAcaAsync(
                        context,
                        command,
                        [
                            "sandbox", "lifecycle", "set",
                            "--id", sandboxId,
                            "--auto-suspend", "disable"
                        ]).ConfigureAwait(false);

                    await lifecycleTask.CompleteAsync("Auto-suspend disabled", CompletionState.Completed, context.CancellationToken).ConfigureAwait(false);
                }
            }

            var portStates = new JsonArray();
            foreach (var endpoint in endpoints)
            {
                var exposeTask = await context.ReportingStep.CreateTaskAsync($"Exposing sandbox port {endpoint.TargetPort}", context.CancellationToken).ConfigureAwait(false);
                await using (exposeTask.ConfigureAwait(false))
                {
                    var portOutput = await AddPortAsync(context, command, sandboxId, endpoint).ConfigureAwait(false);
                    addedPorts.Add(endpoint);

                    var endpointUrl = TryGetJsonString(portOutput, "url") ?? throw new InvalidOperationException("The aca CLI port add response did not contain a URL.");
                    if (endpoint.IsHttp)
                    {
                        await WaitForPublicHttpAsync(endpointUrl, context.CancellationToken).ConfigureAwait(false);
                    }

                    portStates.Add(new JsonObject
                    {
                        ["Name"] = endpoint.Name,
                        ["Port"] = endpoint.TargetPort,
                        ["Url"] = endpointUrl,
                        ["IsExternal"] = endpoint.IsExternal,
                        ["IsHttp"] = endpoint.IsHttp
                    });

                    await exposeTask.CompleteAsync(new MarkdownString($"Public URL: [{endpointUrl}]({endpointUrl})"), CompletionState.Completed, context.CancellationToken).ConfigureAwait(false);
                }
            }

            stateSection.Data.Clear();
            stateSection.Data["SandboxId"] = sandboxId;
            stateSection.Data["DiskImageId"] = diskImageId;
            stateSection.Data["SubscriptionId"] = command.SubscriptionId;
            stateSection.Data["ResourceGroup"] = command.ResourceGroup;
            stateSection.Data["Location"] = command.Region;
            stateSection.Data["SandboxGroup"] = command.SandboxGroup;
            stateSection.Data["Ports"] = portStates;
            await deploymentStateManager.SaveSectionAsync(stateSection, context.CancellationToken).ConfigureAwait(false);

            if (portStates.FirstOrDefault() is JsonObject firstPort && firstPort["Url"]?.GetValue<string>() is { } publicUrl)
            {
                context.Summary.Add(resource.Name, new MarkdownString($"[{publicUrl}]({publicUrl})"));
            }
            else
            {
                context.Summary.Add(resource.Name, new MarkdownString($"Sandbox `{sandboxId}`"));
            }
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(sandboxId))
            {
                await DeleteSandboxAsync(context, command, sandboxId, addedPorts.Select(static endpoint => endpoint.TargetPort), throwOnError: false).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(diskImageId))
            {
                await DeleteDiskImageAsync(context, command, diskImageId, throwOnError: false).ConfigureAwait(false);
            }

            throw;
        }
    }

    private static async Task<string> CreateDiskImageAsync(PipelineStepContext context, AcaCommandContext command, AzureSandboxContainerResource resource, string imageReference, string diskImageName, string deployId)
    {
        var args = new List<string>
        {
            "sandboxgroup", "disk", "create",
            "--image", imageReference,
            "--name", diskImageName,
            "--label", $"aspire-resource={resource.Name}",
            "--label", $"aspire-source={resource.TargetResource.Name}",
            "--label", $"aspire-deploy={deployId}"
        };

        var registry = resource.Parent.ContainerRegistry;
        if (registry is not null)
        {
            var registryEndpoint = await registry.RegistryEndpoint.GetValueAsync(context.CancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(registryEndpoint) &&
                imageReference.StartsWith($"{registryEndpoint}/", StringComparison.OrdinalIgnoreCase))
            {
                var token = await GetAcrRefreshTokenAsync(context, registryEndpoint).ConfigureAwait(false);
                args.Add("--username");
                args.Add(token.Username);
                args.Add("--token");
                args.Add(token.Token);
            }
        }

        return await RunAcaAsync(context, command, args).ConfigureAwait(false);
    }

    private static async Task<AcrRefreshToken> GetAcrRefreshTokenAsync(PipelineStepContext context, string registryEndpoint)
    {
        var azureEnvironment = context.Model.Resources.OfType<AzureEnvironmentResource>().FirstOrDefault() ??
            throw new InvalidOperationException("AzureEnvironmentResource must be present in the application model.");
        var provisioningContext = await azureEnvironment.ProvisioningContextTask.Task.ConfigureAwait(false);
        var tenantId = provisioningContext.Tenant.TenantId?.ToString()
            ?? throw new InvalidOperationException("Tenant ID is required for ACR authentication but was not available in provisioning context.");

        var acrLoginService = context.Services.GetRequiredService<IAcrLoginService>();
        var tokenCredentialProvider = context.Services.GetRequiredService<ITokenCredentialProvider>();

        return await acrLoginService.GetRefreshTokenAsync(
            registryEndpoint,
            tenantId,
            tokenCredentialProvider.TokenCredential,
            context.CancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> CreateSandboxAsync(
        PipelineStepContext context,
        AcaCommandContext command,
        AzureSandboxContainerResource resource,
        string diskImageId,
        IReadOnlyDictionary<string, string> environmentVariables,
        ContainerImageMetadata imageMetadata,
        string deployId)
    {
        var spec = new JsonObject
        {
            ["diskId"] = diskImageId,
            ["labels"] = new JsonObject
            {
                ["aspire-resource"] = resource.Name,
                ["aspire-source"] = resource.TargetResource.Name,
                ["aspire-deploy"] = deployId
            }
        };

        if (environmentVariables.Count > 0)
        {
            var environment = new JsonObject();
            foreach (var (key, value) in environmentVariables.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                environment[key] = value;
            }

            spec["environment"] = environment;
        }

        if (imageMetadata.Entrypoint.Count > 0)
        {
            spec["entrypoint"] = ToJsonArray(imageMetadata.Entrypoint);
        }

        if (imageMetadata.Command.Count > 0)
        {
            spec["cmd"] = ToJsonArray(imageMetadata.Command);
        }

        // `aca sandbox apply` is the CLI shape that accepts structured entrypoint/cmd arrays.
        // The spec can include resolved environment values, so keep it in a temporary directory,
        // pass only the path to the CLI, and delete it immediately after the command returns.
        var tempDirectory = Directory.CreateTempSubdirectory("aspire-sandbox-");
        try
        {
            var specPath = Path.Combine(tempDirectory.FullName, "sandbox.json");
            await File.WriteAllTextAsync(specPath, spec.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), context.CancellationToken).ConfigureAwait(false);

            return await RunAcaAsync(
                context,
                command,
                [
                    "sandbox", "apply",
                    "--file", specPath
                ]).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDirectory.FullName, recursive: true);
            }
            catch (IOException ex)
            {
                context.Logger.LogWarning(ex, "Failed to delete temporary sandbox spec directory '{Directory}'.", tempDirectory.FullName);
            }
            catch (UnauthorizedAccessException ex)
            {
                context.Logger.LogWarning(ex, "Failed to delete temporary sandbox spec directory '{Directory}'.", tempDirectory.FullName);
            }
        }
    }

    private static Task<string> AddPortAsync(PipelineStepContext context, AcaCommandContext command, string sandboxId, SandboxEndpoint endpoint)
    {
        var args = new List<string>
        {
            "sandbox", "port", "add",
            "--id", sandboxId,
            "--port", endpoint.TargetPort.ToString(CultureInfo.InvariantCulture)
        };

        if (endpoint.IsExternal)
        {
            args.Add("--anonymous");
        }

        return RunAcaAsync(context, command, args);
    }

    private static async Task<string> ResolveContainerImageAsync(PipelineStepContext context, AzureSandboxContainerResource resource)
    {
        if (resource.TargetResource.RequiresImageBuildAndPush())
        {
            var containerImageReference = new ContainerImageReference(resource.TargetResource);
            return await ((IValueProvider)containerImageReference)
                .GetValueAsync(new ValueProviderContext { ExecutionContext = context.ExecutionContext, Caller = resource.TargetResource }, context.CancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Could not resolve the pushed container image for resource '{resource.TargetResource.Name}'.");
        }

        if (resource.TargetResource.TryGetContainerImageName(out var imageName))
        {
            return imageName;
        }

        throw new NotSupportedException($"Resource '{resource.TargetResource.Name}' cannot be deployed to Azure sandbox group '{resource.Parent.Name}' because it does not produce or reference a container image.");
    }

    private static async Task<ContainerImageMetadata> ResolveContainerImageMetadataAsync(PipelineStepContext context, IResource resource, string imageReference)
    {
        var (modeledEntrypoint, modeledCommand) = await ResolveModeledCommandAsync(context, resource).ConfigureAwait(false);
        if (!resource.RequiresImageBuildAndPush())
        {
            return new ContainerImageMetadata(modeledEntrypoint ?? [], modeledCommand ?? [], new Dictionary<string, string>(StringComparer.Ordinal));
        }

        var metadata = await InspectLocalContainerImageAsync(context, imageReference).ConfigureAwait(false);
        return metadata with
        {
            Entrypoint = modeledEntrypoint ?? metadata.Entrypoint,
            Command = modeledCommand ?? metadata.Command
        };
    }

    private static async Task<(IReadOnlyList<string>? Entrypoint, IReadOnlyList<string>? Command)> ResolveModeledCommandAsync(PipelineStepContext context, IResource resource)
    {
        if (resource is not ContainerResource container)
        {
            return (null, null);
        }

        var args = new List<object>();
        if (resource.TryGetAnnotationsOfType<CommandLineArgsCallbackAnnotation>(out var callbacks))
        {
            var callbackContext = new CommandLineArgsCallbackContext(args, resource, context.CancellationToken)
            {
                ExecutionContext = context.ExecutionContext,
                Logger = context.Logger
            };

            foreach (var callback in callbacks)
            {
                await callback.Callback(callbackContext).ConfigureAwait(false);
            }
        }

        var resolvedArgs = new List<string>();
        foreach (var arg in args)
        {
            resolvedArgs.Add(await ResolveValueAsync(context, resource, arg).ConfigureAwait(false));
        }

        var entrypoint = string.IsNullOrWhiteSpace(container.Entrypoint) ? null : new[] { container.Entrypoint };
        var command = resolvedArgs.Count == 0 ? null : resolvedArgs;

        return (entrypoint, command);
    }

    private static async Task<ContainerImageMetadata> InspectLocalContainerImageAsync(PipelineStepContext context, string imageReference)
    {
        var runtime = await context.Services.GetRequiredService<IContainerRuntimeResolver>().ResolveAsync(context.CancellationToken).ConfigureAwait(false);
        var runtimeExecutable = runtime.Name switch
        {
            var name when string.Equals(name, "Docker", StringComparison.OrdinalIgnoreCase) => "docker",
            var name when string.Equals(name, "Podman", StringComparison.OrdinalIgnoreCase) => "podman",
            _ => throw new InvalidOperationException($"Unsupported container runtime '{runtime.Name}'.")
        };

        var output = await RunProcessAsync(
            context,
            runtimeExecutable,
            ["image", "inspect", imageReference, "--format", "{{json .Config}}"],
            workingDirectory: null,
            logOutput: false).ConfigureAwait(false);

        return ParseContainerImageMetadata(output, imageReference);
    }

    internal static ContainerImageMetadata ParseContainerImageMetadata(string output, string imageReference)
    {
        JsonNode? config;
        try
        {
            config = JsonNode.Parse(output);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Container runtime returned invalid image metadata for '{imageReference}'.", ex);
        }

        if (config is not JsonObject configObject)
        {
            throw new InvalidOperationException($"Container runtime did not return image metadata for '{imageReference}'.");
        }

        var environmentVariables = new Dictionary<string, string>(StringComparer.Ordinal);
        if (configObject["Env"] is JsonArray environment)
        {
            foreach (var item in environment)
            {
                if (item?.GetValue<string>() is not { } variable)
                {
                    continue;
                }

                var equalsIndex = variable.IndexOf('=');
                if (equalsIndex <= 0)
                {
                    continue;
                }

                environmentVariables[variable[..equalsIndex]] = variable[(equalsIndex + 1)..];
            }
        }

        return new ContainerImageMetadata(
            ReadCommandParts(configObject["Entrypoint"]).ToArray(),
            ReadCommandParts(configObject["Cmd"]).ToArray(),
            environmentVariables);
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static IEnumerable<string> ReadCommandParts(JsonNode? node)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                {
                    if (item?.GetValue<string>() is { } value)
                    {
                        yield return value;
                    }
                }
                break;
            case JsonValue value when value.GetValue<string>() is { } command:
                yield return command;
                break;
        }
    }

    private static async Task<IReadOnlyDictionary<string, string>> ResolveEnvironmentVariablesAsync(PipelineStepContext context, IResource resource)
    {
        var environmentVariables = new Dictionary<string, object>();
        if (resource.TryGetAnnotationsOfType<EnvironmentCallbackAnnotation>(out var callbacks))
        {
            var callbackContext = new EnvironmentCallbackContext(context.ExecutionContext, resource, environmentVariables, context.CancellationToken)
            {
                Logger = context.Logger
            };

            foreach (var callback in callbacks)
            {
                await callback.Callback(callbackContext).ConfigureAwait(false);
            }
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in environmentVariables)
        {
            result[key] = await ResolveValueAsync(context, resource, value).ConfigureAwait(false);
        }

        return result;
    }

    private static async Task<string> ResolveValueAsync(PipelineStepContext context, IResource resource, object? value)
    {
        while (true)
        {
            switch (value)
            {
                case null:
                    return string.Empty;
                case string s:
                    return s;
                case IResourceWithConnectionString connectionStringResource:
                    value = connectionStringResource.ConnectionStringExpression;
                    continue;
                case IValueProvider valueProvider:
                    return await valueProvider
                        .GetValueAsync(new ValueProviderContext { ExecutionContext = context.ExecutionContext, Caller = resource }, context.CancellationToken)
                        .ConfigureAwait(false) ?? string.Empty;
                default:
                    return value.ToString() ?? string.Empty;
            }
        }
    }

    private static async Task DestroyAsync(PipelineStepContext context, AzureSandboxContainerResource resource)
    {
        var deploymentStateManager = context.Services.GetRequiredService<IDeploymentStateManager>();
        var stateSection = await deploymentStateManager.AcquireSectionAsync(GetStateSectionName(resource), context.CancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(stateSection.Data["SandboxId"]?.GetValue<string>()) &&
            string.IsNullOrWhiteSpace(stateSection.Data["DiskImageId"]?.GetValue<string>()))
        {
            await context.ReportingStep.CompleteAsync("No sandbox deployment state found.", CompletionState.Completed, context.CancellationToken).ConfigureAwait(false);
            return;
        }

        var command = new AcaCommandContext(
            GetRequiredStateValue(stateSection, "SubscriptionId"),
            GetRequiredStateValue(stateSection, "ResourceGroup"),
            GetRequiredStateValue(stateSection, "Location"),
            GetRequiredStateValue(stateSection, "SandboxGroup"));

        await DeleteExistingDeploymentAsync(context, command, stateSection, throwOnError: true).ConfigureAwait(false);
        await deploymentStateManager.DeleteSectionAsync(stateSection, context.CancellationToken).ConfigureAwait(false);
    }

    private static async Task DestroyStaleDeploymentsAsync(PipelineStepContext context, IReadOnlySet<string> activeStateSectionNames)
    {
        var deploymentStateManager = context.Services.GetRequiredService<IDeploymentStateManager>();
        var sandboxesSection = await deploymentStateManager.AcquireSectionAsync(SandboxStateParentSection, context.CancellationToken).ConfigureAwait(false);

        var staleResourceNames = sandboxesSection.Data
            .Where(pair => pair.Value is JsonObject)
            .Select(pair => $"{SandboxStateSectionPrefix}{pair.Key}")
            .Where(sectionName => !activeStateSectionNames.Contains(sectionName))
            .ToArray();

        foreach (var sectionName in staleResourceNames)
        {
            var stateSection = await deploymentStateManager.AcquireSectionAsync(sectionName, context.CancellationToken).ConfigureAwait(false);
            var sandboxId = stateSection.Data["SandboxId"]?.GetValue<string>();
            var diskImageId = stateSection.Data["DiskImageId"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(sandboxId) && string.IsNullOrWhiteSpace(diskImageId))
            {
                await deploymentStateManager.DeleteSectionAsync(stateSection, context.CancellationToken).ConfigureAwait(false);
                continue;
            }

            var command = new AcaCommandContext(
                GetRequiredStateValue(stateSection, "SubscriptionId"),
                GetRequiredStateValue(stateSection, "ResourceGroup"),
                GetRequiredStateValue(stateSection, "Location"),
                GetRequiredStateValue(stateSection, "SandboxGroup"));

            var cleanupTask = await context.ReportingStep.CreateTaskAsync($"Deleting stale sandbox deployment {sectionName}", context.CancellationToken).ConfigureAwait(false);
            await using (cleanupTask.ConfigureAwait(false))
            {
                await DeleteExistingDeploymentAsync(context, command, stateSection, throwOnError: true).ConfigureAwait(false);
                await deploymentStateManager.DeleteSectionAsync(stateSection, context.CancellationToken).ConfigureAwait(false);
                await cleanupTask.CompleteAsync($"Deleted stale sandbox deployment {sectionName}", CompletionState.Completed, context.CancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static IReadOnlyList<SandboxEndpoint> ResolveSandboxEndpoints(AzureSandboxContainerResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var endpoints = new Dictionary<int, SandboxEndpoint>();
        foreach (var resolvedEndpoint in resource.TargetResource.ResolveEndpoints())
        {
            if (resolvedEndpoint.TargetPort.Value is not int targetPort)
            {
                throw new InvalidOperationException($"Endpoint '{resolvedEndpoint.Endpoint.Name}' on resource '{resource.TargetResource.Name}' does not have a target port. Configure a target port before deploying it to an Azure sandbox.");
            }

            var transport = resolvedEndpoint.Endpoint.Transport;
            var endpoint = new SandboxEndpoint(
                resolvedEndpoint.Endpoint.Name,
                targetPort,
                resolvedEndpoint.Endpoint.IsExternal,
                transport is "http" or "http2");

            if (endpoints.TryGetValue(targetPort, out var existingEndpoint))
            {
                endpoints[targetPort] = existingEndpoint with
                {
                    IsExternal = existingEndpoint.IsExternal || endpoint.IsExternal,
                    IsHttp = existingEndpoint.IsHttp || endpoint.IsHttp
                };
            }
            else
            {
                endpoints.Add(targetPort, endpoint);
            }
        }

        return [.. endpoints.Values.OrderBy(static endpoint => endpoint.TargetPort)];
    }

    private static async Task DeleteExistingDeploymentAsync(PipelineStepContext context, AcaCommandContext command, DeploymentStateSection stateSection, bool throwOnError)
    {
        var sandboxId = stateSection.Data["SandboxId"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(sandboxId))
        {
            await DeleteSandboxAsync(context, command, sandboxId, GetStatePorts(stateSection), throwOnError).ConfigureAwait(false);
        }

        var diskImageId = stateSection.Data["DiskImageId"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(diskImageId))
        {
            await DeleteDiskImageAsync(context, command, diskImageId, throwOnError).ConfigureAwait(false);
        }
    }

    private static IEnumerable<int> GetStatePorts(DeploymentStateSection stateSection)
    {
        if (stateSection.Data["Ports"] is JsonArray ports)
        {
            foreach (var port in ports.OfType<JsonObject>())
            {
                if (port["Port"]?.GetValue<int>() is { } portNumber)
                {
                    yield return portNumber;
                }
            }

            yield break;
        }

        if (stateSection.Data["Port"]?.GetValue<int>() is { } legacyPort)
        {
            yield return legacyPort;
        }
    }

    private static async Task EnsureSandboxGroupDataOwnerAsync(PipelineStepContext context, AcaCommandContext command)
    {
        var roleTask = await context.ReportingStep.CreateTaskAsync("Ensuring sandbox group data-plane access", context.CancellationToken).ConfigureAwait(false);
        await using (roleTask.ConfigureAwait(false))
        {
            var principalId = await GetCurrentAzurePrincipalIdAsync(context).ConfigureAwait(false);

            await RunAcaAsync(
                context,
                command,
                [
                    "sandboxgroup", "role", "create",
                    "--role", SandboxGroupDataOwnerRole,
                    "--principal-id", principalId
                ]).ConfigureAwait(false);

            await roleTask.CompleteAsync("Sandbox group data-plane access is ready", CompletionState.Completed, context.CancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> GetCurrentAzurePrincipalIdAsync(PipelineStepContext context)
    {
        var accessToken = (await RunProcessAsync(
            context,
            "az",
            ["account", "get-access-token", "--query", "accessToken", "-o", "tsv"],
            workingDirectory: null,
            logOutput: false,
            includeOutputInException: false).ConfigureAwait(false)).Trim();

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("Azure CLI did not return an access token for the current account.");
        }

        return GetPrincipalIdFromAccessToken(accessToken);
    }

    private static string GetPrincipalIdFromAccessToken(string accessToken)
    {
        var parts = accessToken.Split('.');
        if (parts.Length < 2)
        {
            throw new InvalidOperationException("Azure CLI returned an access token that is not a JWT.");
        }

        // Azure access tokens are JWTs with the current user's object ID in the payload:
        //   <base64url header>.<base64url {"oid":"<principal-id>",...}>.<signature>
        // The value is used for the sandbox data-plane role assignment; never log the token.
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');

        byte[] payloadBytes;
        try
        {
            payloadBytes = Convert.FromBase64String(payload);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Azure CLI returned an access token with an invalid JWT payload.", ex);
        }

        using var document = JsonDocument.Parse(payloadBytes);
        if (!document.RootElement.TryGetProperty("oid", out var oidElement) ||
            oidElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(oidElement.GetString()))
        {
            throw new InvalidOperationException("Azure CLI access token did not contain an 'oid' principal claim.");
        }

        return oidElement.GetString()!;
    }

    private static async Task WaitForPublicHttpAsync(string publicUrl, CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(PublicEndpointTimeoutSeconds);
        Exception? lastException = null;
        HttpStatusCode? lastStatusCode = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await httpClient.GetAsync(publicUrl.TrimEnd('/'), cancellationToken).ConfigureAwait(false);
                lastStatusCode = response.StatusCode;
                if ((int)response.StatusCode < 500)
                {
                    return;
                }
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = ex;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Sandbox public URL '{publicUrl}' was not ready after {PublicEndpointTimeoutSeconds} seconds (last HTTP status: '{lastStatusCode}').", lastException);
    }

    private static async Task DeleteSandboxAsync(
        PipelineStepContext context,
        AcaCommandContext command,
        string sandboxId,
        IEnumerable<int> ports,
        bool throwOnError)
    {
        foreach (var port in ports.Distinct())
        {
            await RunAcaAsync(
                context,
                command,
                [
                    "sandbox", "port", "remove",
                    "--id", sandboxId,
                    "--port", port.ToString(CultureInfo.InvariantCulture)
                ],
                throwOnError: false).ConfigureAwait(false);
        }

        await RunAcaAsync(
            context,
            command,
            [
                "sandbox", "delete",
                "--id", sandboxId,
                "--yes"
            ],
            throwOnError).ConfigureAwait(false);
    }

    private static Task DeleteDiskImageAsync(PipelineStepContext context, AcaCommandContext command, string diskImageId, bool throwOnError)
    {
        return RunAcaAsync(
            context,
            command,
            [
                "sandboxgroup", "disk", "delete",
                "--id", diskImageId
            ],
            throwOnError);
    }

    private static Task<string> RunAcaAsync(PipelineStepContext context, AcaCommandContext command, IReadOnlyList<string> arguments, bool throwOnError = true)
    {
        var fullArguments = new List<string>
        {
            "--subscription", command.SubscriptionId,
            "--resource-group", command.ResourceGroup,
            "--sandbox-group", command.SandboxGroup,
            "--region", command.Region,
            "--output", "json"
        };
        fullArguments.AddRange(arguments);

        return RunProcessAsync(context, "aca", fullArguments, workingDirectory: null, throwOnError);
    }

    private static async Task<string> RunProcessAsync(
        PipelineStepContext context,
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        bool throwOnError = true,
        bool logOutput = true,
        bool includeOutputInException = true)
    {
        using var process = new Process();
        process.StartInfo.FileName = fileName;
        process.StartInfo.WorkingDirectory = workingDirectory ?? string.Empty;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        context.Logger.LogInformation("Running command: {Command} {Arguments}", fileName, FormatProcessArgumentsForDisplay(arguments));

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"Failed to start '{fileName}'. Ensure it is installed and available on PATH.", ex);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(context.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            await TerminateProcessOnCancellationAsync(context, process, fileName).ConfigureAwait(false);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (logOutput && !string.IsNullOrWhiteSpace(stdout))
        {
            context.Logger.LogInformation("{Command} stdout: {Stdout}", fileName, stdout.Trim());
        }

        if (logOutput && !string.IsNullOrWhiteSpace(stderr))
        {
            context.Logger.LogWarning("{Command} stderr: {Stderr}", fileName, stderr.Trim());
        }

        if (throwOnError && process.ExitCode != 0)
        {
            var output = includeOutputInException
                ? string.IsNullOrWhiteSpace(stderr) ? stdout : stderr
                : string.Empty;
            throw new InvalidOperationException($"Command '{fileName}' failed with exit code {process.ExitCode}.{Environment.NewLine}{output}");
        }

        return stdout;
    }

    private static async Task TerminateProcessOnCancellationAsync(PipelineStepContext context, Process process, string fileName)
    {
        try
        {
            if (!process.HasExited)
            {
                context.Logger.LogInformation("Terminating cancelled command: {Command}", fileName);
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
            // The process can exit between HasExited and Kill/WaitForExitAsync.
        }
        catch (Win32Exception ex)
        {
            context.Logger.LogWarning(ex, "Failed to terminate cancelled command: {Command}", fileName);
        }
    }

    private static async Task<AzureDeploymentState> GetAzureStateAsync(IDeploymentStateManager deploymentStateManager, CancellationToken cancellationToken)
    {
        var azureState = await deploymentStateManager.AcquireSectionAsync("Azure", cancellationToken).ConfigureAwait(false);
        return new AzureDeploymentState(
            GetRequiredStateValue(azureState, "SubscriptionId"),
            GetRequiredStateValue(azureState, "ResourceGroup"),
            GetRequiredStateValue(azureState, "Location"));
    }

    private static string GetRequiredStateValue(DeploymentStateSection section, string name)
    {
        var value = section.Data[name]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Deployment state section '{section.SectionName}' is missing required value '{name}'.");
        }

        return value;
    }

    private static string GetRequiredOutput(AzureBicepResource resource, string name)
    {
        if (!resource.Outputs.TryGetValue(name, out var value) || value is null || string.IsNullOrWhiteSpace(value.ToString()))
        {
            throw new InvalidOperationException($"Azure resource '{resource.Name}' is missing required output '{name}'. Ensure Azure infrastructure provisioning completed successfully.");
        }

        return value.ToString()!;
    }

    private static async Task<string> FindSandboxByLabelAsync(PipelineStepContext context, AcaCommandContext command, string deployId)
    {
        var listOutput = await RunAcaAsync(
            context,
            command,
            [
                "sandbox", "list",
                "--selector", $"aspire-deploy={deployId}"
            ]).ConfigureAwait(false);

        return TryGetSandboxId(listOutput) ?? string.Empty;
    }

    private static string? TryGetSandboxId(string output)
    {
        // The aca CLI has returned both human text:
        //   Created sandbox: <id>
        // and JSON shapes:
        //   { "id": "<id>", ... }
        //   [{ "id": "<id>", ... }]
        //   { "items": [{ "id": "<id>", ... }] }
        var jsonId = TryGetJsonString(output, "id")
            ?? TryGetJsonString(output, "sandboxId")
            ?? TryGetJsonString(output, "sandbox_id");
        if (!string.IsNullOrWhiteSpace(jsonId))
        {
            return jsonId;
        }

        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            const string prefix = "Created sandbox:";
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return line[prefix.Length..].Trim();
            }
        }

        return null;
    }

    private static string? TryGetJsonString(string output, string propertyName)
    {
        try
        {
            var node = JsonNode.Parse(output);
            return TryGetJsonString(node, propertyName);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryGetJsonString(JsonNode? node, string propertyName)
    {
        return node switch
        {
            JsonObject obj when obj[propertyName]?.GetValue<string>() is { } value => value,
            JsonObject obj when obj["items"] is JsonArray items => items.Select(item => TryGetJsonString(item, propertyName)).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)),
            JsonArray array => array.Select(item => TryGetJsonString(item, propertyName)).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)),
            _ => null
        };
    }

    private static string CreateSandboxResourceName(string resourceName, string deployId)
    {
        var normalized = new string(resourceName.ToLowerInvariant().Select(static c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "app";
        }

        if (normalized.Length > 32)
        {
            normalized = normalized[..32].Trim('-');
        }

        return $"{normalized}-{deployId[..8]}";
    }

    private static string FormatProcessArgumentsForDisplay(IReadOnlyList<string> arguments)
    {
        var formattedArguments = new string[arguments.Count];
        for (var i = 0; i < arguments.Count; i++)
        {
            formattedArguments[i] = i > 0 && string.Equals(arguments[i - 1], "--token", StringComparison.OrdinalIgnoreCase) ? "******" :
                i > 0 && string.Equals(arguments[i - 1], "--env", StringComparison.OrdinalIgnoreCase) ? FormatEnvironmentArgumentForDisplay(arguments[i]) :
                FormatProcessArgumentForDisplay(arguments[i]);
        }

        return string.Join(' ', formattedArguments);
    }

    private static string FormatEnvironmentArgumentForDisplay(string argument)
    {
        var equalsIndex = argument.IndexOf('=');
        return equalsIndex > 0
            ? FormatProcessArgumentForDisplay($"{argument[..(equalsIndex + 1)]}******")
            : "******";
    }

    private static string FormatProcessArgumentForDisplay(string argument)
    {
        if (argument.Length > 0 && !argument.Any(static c => char.IsWhiteSpace(c) || c == '"'))
        {
            return argument;
        }

        var builder = new StringBuilder(argument.Length + 2);
        builder.Append('"');

        var backslashCount = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashCount++;
                continue;
            }

            if (c == '"')
            {
                builder.Append('\\', backslashCount * 2 + 1);
                builder.Append('"');
            }
            else
            {
                builder.Append('\\', backslashCount);
                builder.Append(c);
            }

            backslashCount = 0;
        }

        builder.Append('\\', backslashCount * 2);
        builder.Append('"');

        return builder.ToString();
    }

    internal static string GetStateSectionName(AzureSandboxContainerResource resource) => $"{SandboxStateSectionPrefix}{resource.Name}";

    private static string GetStaleCleanupStepName(AzureSandboxGroupResource resource) => $"destroy-stale-azure-sandboxes-{resource.Name}";

    private static string GetDeployStepName(AzureSandboxContainerResource resource) => $"deploy-{resource.Name}";

    private static string GetDestroyStepName(AzureSandboxContainerResource resource) => $"destroy-{resource.Name}";

    internal readonly record struct SandboxEndpoint(string Name, int TargetPort, bool IsExternal, bool IsHttp);

    internal sealed record ContainerImageMetadata(IReadOnlyList<string> Entrypoint, IReadOnlyList<string> Command, IReadOnlyDictionary<string, string> EnvironmentVariables);

    private sealed record AzureDeploymentState(string SubscriptionId, string ResourceGroup, string Location);

    private sealed record AcaCommandContext(string SubscriptionId, string ResourceGroup, string Region, string SandboxGroup);
}
