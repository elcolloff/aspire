// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES002
#pragma warning disable ASPIREPIPELINES004
#pragma warning disable ASPIREAZURE001

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.JavaScript;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Azure;

internal static class AzureSandboxStaticSiteDeployment
{
    private const string SandboxStateParentSection = "Azure:Sandboxes";
    internal const string SandboxStateSectionPrefix = $"{SandboxStateParentSection}:";
    private const string SandboxRoot = "/app";
    private const string SandboxWwwRoot = "/app/wwwroot";
    private const string SandboxGroupDataOwnerRole = "Container Apps SandboxGroup Data Owner";
    private const int ReadinessTimeoutSeconds = 30;
    private const int PublicEndpointTimeoutSeconds = 60;

    public static IEnumerable<PipelineStep> CreatePipelineSteps(AzureSandboxStaticSiteResource resource)
    {
        var deployStepName = GetDeployStepName(resource);
        var destroyStepName = GetDestroyStepName(resource);

        return
        [
            new PipelineStep
            {
                Name = deployStepName,
                Description = $"Deploys JavaScript static site '{resource.Source.Name}' to ACA sandbox '{resource.Name}'.",
                Action = context => DeployAsync(context, resource),
                DependsOnSteps = [AzureEnvironmentResource.ProvisionInfrastructureStepName, WellKnownPipelineSteps.DeployPrereq],
                RequiredBySteps = [WellKnownPipelineSteps.Deploy],
                Tags = [WellKnownPipelineTags.DeployCompute],
                Resource = resource
            },
            new PipelineStep
            {
                Name = destroyStepName,
                Description = $"Deletes ACA sandbox static site '{resource.Name}'.",
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
            Description = $"Deletes stale ACA sandbox static-site deployments for Azure sandbox group '{resource.Name}'.",
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
                if (deploymentTargetAnnotation.DeploymentTarget is AzureSandboxStaticSiteResource staticSite)
                {
                    activeStateSectionNames.Add(GetStateSectionName(staticSite));
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

    public static void ConfigureDestroyOrdering(PipelineConfigurationContext context, AzureSandboxStaticSiteResource resource)
    {
        var destroyStepName = GetDestroyStepName(resource);

        foreach (var step in context.Steps.Where(static step => step.Name.StartsWith("destroy-azure-", StringComparison.Ordinal)))
        {
            step.DependsOn(destroyStepName);
        }
    }

    private static async Task DeployAsync(PipelineStepContext context, AzureSandboxStaticSiteResource resource)
    {
        var staticFilesPath = Path.GetFullPath(resource.OutputPath, resource.SourceWorkingDirectory);
        var endpoint = ResolveSandboxEndpoint(resource);
        var deploymentStateManager = context.Services.GetRequiredService<IDeploymentStateManager>();
        var azureState = await GetAzureStateAsync(deploymentStateManager, context.CancellationToken).ConfigureAwait(false);

        var sandboxGroupName = GetRequiredOutput(resource.Parent, "name");
        var command = new AcaCommandContext(azureState.SubscriptionId, azureState.ResourceGroup, azureState.Location, sandboxGroupName);

        if (resource.Build)
        {
            await BuildStaticSiteAsync(context, resource).ConfigureAwait(false);
        }

        if (!Directory.Exists(staticFilesPath))
        {
            throw new DirectoryNotFoundException($"Static site output directory '{staticFilesPath}' was not found.");
        }

        var stateSection = await deploymentStateManager.AcquireSectionAsync(GetStateSectionName(resource), context.CancellationToken).ConfigureAwait(false);
        var previousSandboxId = stateSection.Data["SandboxId"]?.GetValue<string>();

        await EnsureSandboxGroupDataOwnerAsync(context, command).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(previousSandboxId))
        {
            await DeleteSandboxAsync(context, command, previousSandboxId, endpoint.TargetPort, removePort: true, throwOnError: false).ConfigureAwait(false);
            await deploymentStateManager.DeleteSectionAsync(stateSection, context.CancellationToken).ConfigureAwait(false);
            stateSection = await deploymentStateManager.AcquireSectionAsync(GetStateSectionName(resource), context.CancellationToken).ConfigureAwait(false);
        }

        var deployId = Guid.NewGuid().ToString("N");
        var sandboxId = string.Empty;
        var portAdded = false;
        try
        {
            var createTask = await context.ReportingStep.CreateTaskAsync($"Creating sandbox for {resource.Name}", context.CancellationToken).ConfigureAwait(false);
            await using (createTask.ConfigureAwait(false))
            {
                var createOutput = await RunAcaAsync(
                    context,
                    command,
                    [
                        "sandbox", "create",
                        "--disk", resource.Disk,
                        "--label", $"aspire-resource={resource.Name}",
                        "--label", $"aspire-source={resource.Source.Name}",
                        "--label", $"aspire-deploy={deployId}"
                    ]).ConfigureAwait(false);

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

            var uploadTask = await context.ReportingStep.CreateTaskAsync($"Uploading static files from {staticFilesPath}", context.CancellationToken).ConfigureAwait(false);
            await using (uploadTask.ConfigureAwait(false))
            {
                await UploadStaticSiteAsync(context, command, sandboxId, staticFilesPath, endpoint.TargetPort).ConfigureAwait(false);
                await uploadTask.CompleteAsync("Static files uploaded", CompletionState.Completed, context.CancellationToken).ConfigureAwait(false);
            }

            var startTask = await context.ReportingStep.CreateTaskAsync($"Starting static server on port {endpoint.TargetPort}", context.CancellationToken).ConfigureAwait(false);
            await using (startTask.ConfigureAwait(false))
            {
                await RunAcaAsync(
                    context,
                    command,
                    [
                        "sandbox", "exec",
                        "--id", sandboxId,
                        "--command", $"cd {SandboxRoot} && nohup node server.js > /tmp/aspire-static-site.log 2>&1 &"
                    ]).ConfigureAwait(false);

                await WaitForSandboxHttpAsync(context, command, sandboxId, endpoint.TargetPort).ConfigureAwait(false);
                await startTask.CompleteAsync("Static server is ready", CompletionState.Completed, context.CancellationToken).ConfigureAwait(false);
            }

            var exposeTask = await context.ReportingStep.CreateTaskAsync($"Exposing sandbox port {endpoint.TargetPort}", context.CancellationToken).ConfigureAwait(false);
            string publicUrl;
            await using (exposeTask.ConfigureAwait(false))
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

                var portOutput = await RunAcaAsync(context, command, args).ConfigureAwait(false);
                portAdded = true;
                publicUrl = TryGetJsonString(portOutput, "url") ?? throw new InvalidOperationException("The aca CLI port add response did not contain a URL.");

                await WaitForPublicHttpAsync(publicUrl, context.CancellationToken).ConfigureAwait(false);
                await exposeTask.CompleteAsync(new MarkdownString($"Public URL: [{publicUrl}]({publicUrl})"), CompletionState.Completed, context.CancellationToken).ConfigureAwait(false);
            }

            stateSection.Data.Clear();
            stateSection.Data["SandboxId"] = sandboxId;
            stateSection.Data["Url"] = publicUrl;
            stateSection.Data["Port"] = endpoint.TargetPort;
            stateSection.Data["EndpointName"] = endpoint.Name;
            stateSection.Data["SubscriptionId"] = command.SubscriptionId;
            stateSection.Data["ResourceGroup"] = command.ResourceGroup;
            stateSection.Data["Location"] = command.Region;
            stateSection.Data["SandboxGroup"] = command.SandboxGroup;
            await deploymentStateManager.SaveSectionAsync(stateSection, context.CancellationToken).ConfigureAwait(false);

            context.Summary.Add(resource.Name, new MarkdownString($"[{publicUrl}]({publicUrl})"));
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(sandboxId))
            {
                await DeleteSandboxAsync(context, command, sandboxId, endpoint.TargetPort, portAdded, throwOnError: false).ConfigureAwait(false);
            }

            throw;
        }
    }

    private static async Task DestroyAsync(PipelineStepContext context, AzureSandboxStaticSiteResource resource)
    {
        var deploymentStateManager = context.Services.GetRequiredService<IDeploymentStateManager>();
        var stateSection = await deploymentStateManager.AcquireSectionAsync(GetStateSectionName(resource), context.CancellationToken).ConfigureAwait(false);

        var sandboxId = stateSection.Data["SandboxId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(sandboxId))
        {
            await context.ReportingStep.CompleteAsync("No sandbox static-site deployment state found.", CompletionState.Completed, context.CancellationToken).ConfigureAwait(false);
            return;
        }

        var command = new AcaCommandContext(
            GetRequiredStateValue(stateSection, "SubscriptionId"),
            GetRequiredStateValue(stateSection, "ResourceGroup"),
            GetRequiredStateValue(stateSection, "Location"),
            GetRequiredStateValue(stateSection, "SandboxGroup"));

        var port = stateSection.Data["Port"]?.GetValue<int>() ?? ResolveSandboxEndpoint(resource).TargetPort;
        await DeleteSandboxAsync(context, command, sandboxId, port, removePort: true, throwOnError: true).ConfigureAwait(false);
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
            if (string.IsNullOrWhiteSpace(sandboxId))
            {
                await deploymentStateManager.DeleteSectionAsync(stateSection, context.CancellationToken).ConfigureAwait(false);
                continue;
            }

            var command = new AcaCommandContext(
                GetRequiredStateValue(stateSection, "SubscriptionId"),
                GetRequiredStateValue(stateSection, "ResourceGroup"),
                GetRequiredStateValue(stateSection, "Location"),
                GetRequiredStateValue(stateSection, "SandboxGroup"));
            var port = stateSection.Data["Port"]?.GetValue<int>();

            var cleanupTask = await context.ReportingStep.CreateTaskAsync($"Deleting stale sandbox deployment {sectionName}", context.CancellationToken).ConfigureAwait(false);
            await using (cleanupTask.ConfigureAwait(false))
            {
                await DeleteSandboxAsync(context, command, sandboxId, port.GetValueOrDefault(), removePort: port.HasValue, throwOnError: true).ConfigureAwait(false);
                await deploymentStateManager.DeleteSectionAsync(stateSection, context.CancellationToken).ConfigureAwait(false);
                await cleanupTask.CompleteAsync($"Deleted stale sandbox {sandboxId}", CompletionState.Completed, context.CancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static SandboxEndpoint ResolveSandboxEndpoint(AzureSandboxStaticSiteResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var resolvedEndpoint = resource.Source.ResolveEndpoints()
            .SingleOrDefault(endpoint => string.Equals(endpoint.Endpoint.Name, resource.EndpointName, StringComparison.OrdinalIgnoreCase));

        if (resolvedEndpoint is null)
        {
            throw new InvalidOperationException($"Endpoint '{resource.EndpointName}' was not found on resource '{resource.Source.Name}'. Configure the source resource with PublishAsStaticWebsite or WithHttpEndpoint before deploying it to an Azure sandbox static site.");
        }

        if (resolvedEndpoint.Endpoint.Transport is not ("http" or "http2"))
        {
            throw new NotSupportedException($"Endpoint '{resource.EndpointName}' on resource '{resource.Source.Name}' uses transport '{resolvedEndpoint.Endpoint.Transport}'. Azure sandbox static sites require an HTTP endpoint.");
        }

        if (resolvedEndpoint.TargetPort.Value is not int targetPort)
        {
            throw new InvalidOperationException($"Endpoint '{resource.EndpointName}' on resource '{resource.Source.Name}' does not have a target port. Configure a target port before deploying it to an Azure sandbox static site.");
        }

        return new SandboxEndpoint(resolvedEndpoint.Endpoint.Name, targetPort, resolvedEndpoint.Endpoint.IsExternal);
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

    private static async Task BuildStaticSiteAsync(PipelineStepContext context, AzureSandboxStaticSiteResource resource)
    {
        var packageManager = resource.Source.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManagerAnnotation)
            ? packageManagerAnnotation
            : null;

        if (packageManager is null)
        {
            return;
        }

        if (resource.Source.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out var installCommand))
        {
            var installTask = await context.ReportingStep.CreateTaskAsync($"Installing JavaScript dependencies for {resource.Source.Name}", context.CancellationToken).ConfigureAwait(false);
            await using (installTask.ConfigureAwait(false))
            {
                await RunProcessAsync(
                    context,
                    packageManager.ExecutableName,
                    installCommand.Args,
                    resource.SourceWorkingDirectory).ConfigureAwait(false);

                await installTask.CompleteAsync("Dependencies installed", CompletionState.Completed, context.CancellationToken).ConfigureAwait(false);
            }
        }

        if (resource.Source.TryGetLastAnnotation<JavaScriptBuildScriptAnnotation>(out var buildCommand))
        {
            var buildArgs = new List<string>();
            if (!string.IsNullOrWhiteSpace(packageManager.ScriptCommand))
            {
                buildArgs.Add(packageManager.ScriptCommand);
            }

            buildArgs.Add(buildCommand.ScriptName);
            buildArgs.AddRange(buildCommand.Args);

            var buildTask = await context.ReportingStep.CreateTaskAsync($"Building static files for {resource.Source.Name}", context.CancellationToken).ConfigureAwait(false);
            await using (buildTask.ConfigureAwait(false))
            {
                await RunProcessAsync(
                    context,
                    packageManager.ExecutableName,
                    buildArgs,
                    resource.SourceWorkingDirectory).ConfigureAwait(false);

                await buildTask.CompleteAsync("Static files built", CompletionState.Completed, context.CancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task UploadStaticSiteAsync(PipelineStepContext context, AcaCommandContext command, string sandboxId, string staticFilesPath, int port)
    {
        await RunAcaAsync(
            context,
            command,
            [
                "sandbox", "exec",
                "--id", sandboxId,
                "--command", $"rm -rf {SandboxWwwRoot} && mkdir -p {SandboxWwwRoot}"
            ]).ConfigureAwait(false);

        var outputService = context.Services.GetRequiredService<IPipelineOutputService>();
        var tempDirectory = outputService.GetTempDirectory();
        Directory.CreateDirectory(tempDirectory);

        var serverPath = Path.Combine(tempDirectory, $"aspire-sandbox-static-server-{Guid.NewGuid():N}.js");
        await File.WriteAllTextAsync(serverPath, CreateStaticServerScript(port), context.CancellationToken).ConfigureAwait(false);

        try
        {
            await WriteSandboxFileAsync(context, command, sandboxId, serverPath, $"{SandboxRoot}/server.js").ConfigureAwait(false);

            foreach (var directory in Directory.EnumerateDirectories(staticFilesPath, "*", SearchOption.AllDirectories))
            {
                var relativeDirectory = ToSandboxRelativePath(staticFilesPath, directory);
                await RunAcaAsync(
                    context,
                    command,
                    [
                        "sandbox", "exec",
                        "--id", sandboxId,
                        "--command", $"mkdir -p {ShellQuote($"{SandboxWwwRoot}/{relativeDirectory}")}"
                    ]).ConfigureAwait(false);
            }

            foreach (var file in Directory.EnumerateFiles(staticFilesPath, "*", SearchOption.AllDirectories))
            {
                var relativeFile = ToSandboxRelativePath(staticFilesPath, file);
                await WriteSandboxFileAsync(context, command, sandboxId, file, $"{SandboxWwwRoot}/{relativeFile}").ConfigureAwait(false);
            }
        }
        finally
        {
            File.Delete(serverPath);
        }
    }

    private static Task WriteSandboxFileAsync(PipelineStepContext context, AcaCommandContext command, string sandboxId, string sourcePath, string targetPath)
    {
        return RunAcaAsync(
            context,
            command,
            [
                "sandbox", "fs", "write",
                "--id", sandboxId,
                "--path", targetPath,
                "--file", sourcePath
            ]);
    }

    private static async Task WaitForSandboxHttpAsync(PipelineStepContext context, AcaCommandContext command, string sandboxId, int port)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(ReadinessTimeoutSeconds);
        string? lastStatus = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            lastStatus = (await RunAcaAsync(
                context,
                command,
                [
                    "sandbox", "exec",
                    "--id", sandboxId,
                    "--command", $"curl -fsS -o /dev/null -w '%{{http_code}}' http://localhost:{port}/healthz || true"
                ],
                throwOnError: false).ConfigureAwait(false)).Trim();

            if (lastStatus == "200")
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), context.CancellationToken).ConfigureAwait(false);
        }

        var log = await RunAcaAsync(
            context,
            command,
            [
                "sandbox", "exec",
                "--id", sandboxId,
                "--command", "cat /tmp/aspire-static-site.log 2>/dev/null || true"
            ],
            throwOnError: false).ConfigureAwait(false);

        throw new TimeoutException($"Sandbox static server was not ready after {ReadinessTimeoutSeconds} seconds (last HTTP status: '{lastStatus}'). Server log:{Environment.NewLine}{log}");
    }

    private static async Task WaitForPublicHttpAsync(string publicUrl, CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(PublicEndpointTimeoutSeconds);
        Exception? lastException = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await httpClient.GetAsync($"{publicUrl.TrimEnd('/')}/healthz", cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
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

        throw new TimeoutException($"Sandbox public URL '{publicUrl}' was not ready after {PublicEndpointTimeoutSeconds} seconds.", lastException);
    }

    private static async Task DeleteSandboxAsync(
        PipelineStepContext context,
        AcaCommandContext command,
        string sandboxId,
        int port,
        bool removePort,
        bool throwOnError)
    {
        if (removePort)
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

        context.Logger.LogInformation("Running command: {Command} {Arguments}", fileName, string.Join(' ', arguments.Select(FormatProcessArgumentForDisplay)));

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

    private static string ToSandboxRelativePath(string root, string path)
    {
        return Path.GetRelativePath(root, path)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static string ShellQuote(string value)
    {
        return $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
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

    private static string CreateStaticServerScript(int port)
    {
        return $$"""
            const http = require('http');
            const fs = require('fs');
            const path = require('path');

            const port = Number(process.env.PORT || '{{port}}');
            const root = path.resolve(__dirname, 'wwwroot');
            const contentTypes = new Map([
              ['.css', 'text/css; charset=utf-8'],
              ['.gif', 'image/gif'],
              ['.html', 'text/html; charset=utf-8'],
              ['.ico', 'image/x-icon'],
              ['.jpg', 'image/jpeg'],
              ['.jpeg', 'image/jpeg'],
              ['.js', 'text/javascript; charset=utf-8'],
              ['.json', 'application/json; charset=utf-8'],
              ['.png', 'image/png'],
              ['.svg', 'image/svg+xml'],
              ['.txt', 'text/plain; charset=utf-8'],
              ['.wasm', 'application/wasm'],
              ['.webp', 'image/webp']
            ]);

            function send(res, status, headers, body) {
              res.writeHead(status, headers);
              res.end(body);
            }

            function resolveFile(urlPath) {
              let decoded;
              try {
                decoded = decodeURIComponent(urlPath.split('?')[0]);
              } catch {
                return null;
              }

              const normalized = path.normalize(decoded).replace(/^(\.\.[/\\])+/, '');
              let filePath = path.join(root, normalized);
              if (!filePath.startsWith(root)) {
                return null;
              }

              if (fs.existsSync(filePath) && fs.statSync(filePath).isDirectory()) {
                filePath = path.join(filePath, 'index.html');
              }

              if (fs.existsSync(filePath) && fs.statSync(filePath).isFile()) {
                return filePath;
              }

              const fallback = path.join(root, 'index.html');
              return fs.existsSync(fallback) ? fallback : null;
            }

            const server = http.createServer((req, res) => {
              if (req.url === '/healthz') {
                send(res, 200, { 'content-type': 'application/json; charset=utf-8' }, JSON.stringify({ status: 'ok' }));
                return;
              }

              const filePath = resolveFile(req.url || '/');
              if (!filePath) {
                send(res, 404, { 'content-type': 'text/plain; charset=utf-8' }, 'Not found');
                return;
              }

              const contentType = contentTypes.get(path.extname(filePath).toLowerCase()) || 'application/octet-stream';
              send(res, 200, { 'content-type': contentType, 'cache-control': 'no-store' }, fs.readFileSync(filePath));
            });

            server.listen(port, '0.0.0.0', () => {
              console.log(`Static site listening on ${port}`);
            });
            """;
    }

    internal static string GetStateSectionName(AzureSandboxStaticSiteResource resource) => $"{SandboxStateSectionPrefix}{resource.Name}";

    private static string GetStaleCleanupStepName(AzureSandboxGroupResource resource) => $"destroy-stale-azure-sandboxes-{resource.Name}";

    private static string GetDeployStepName(AzureSandboxStaticSiteResource resource) => $"deploy-{resource.Name}";

    private static string GetDestroyStepName(AzureSandboxStaticSiteResource resource) => $"destroy-{resource.Name}";

    internal readonly record struct SandboxEndpoint(string Name, int TargetPort, bool IsExternal);

    private sealed record AzureDeploymentState(string SubscriptionId, string ResourceGroup, string Location);

    private sealed record AcaCommandContext(string SubscriptionId, string ResourceGroup, string Region, string SandboxGroup);
}
