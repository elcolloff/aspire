// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREAZURE001
#pragma warning disable ASPIREAZURE003

using System.Net;
using System.Text;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Azure.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Azure.Tests;

public class AzureSandboxesTests
{
    [Fact]
    public async Task AddAzureSandboxResourcesGeneratesBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var gateway = builder.AddAzureConnectorGateway("gateway");
        gateway.AddConnection("office365", "Office365", displayName: "Office 365 (Outlook)");
        var teams = gateway.AddConnection("teams", "a365teamsmcp", displayName: "Microsoft Teams (Work IQ MCP)");
        gateway.AddMcpServerConfig("teamsmcp", "Microsoft Teams MCP server")
            .WithConnector(
                "a365teamsmcp",
                teams,
                "mcp_TeamsServer",
                displayName: "Microsoft Teams MCP Server",
                description: "Upstream MCP endpoint that proxies JSON-RPC traffic to the Work IQ Teams MCP server.");

        var hostIdentity = builder.AddAzureUserAssignedIdentity("hostmi");
        var hostGroup = builder.AddAzureSandboxGroup("hostgroup")
            .WithUserAssignedIdentity(hostIdentity);
        var workerGroup = builder.AddAzureSandboxGroup("workergroup");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var (gatewayManifest, gatewayBicep) = await AzureManifestUtils.GetManifestWithBicep(model, gateway.Resource);
        var (hostGroupManifest, hostGroupBicep) = await AzureManifestUtils.GetManifestWithBicep(model, hostGroup.Resource);
        var (workerGroupManifest, workerGroupBicep) = await AzureManifestUtils.GetManifestWithBicep(model, workerGroup.Resource);

        await Verify(gatewayManifest.ToString(), "json")
            .AppendContentAsFile(gatewayBicep, "bicep")
            .AppendContentAsFile(hostGroupManifest.ToString(), "json")
            .AppendContentAsFile(hostGroupBicep, "bicep")
            .AppendContentAsFile(workerGroupManifest.ToString(), "json")
            .AppendContentAsFile(workerGroupBicep, "bicep");
    }

    [Fact]
    public async Task WithRoleAssignmentsAddsSandboxGroupRoleAssignments()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.Services.Configure<AzureProvisioningOptions>(options => options.SupportsTargetedRoleAssignments = true);

        var identity = builder.AddAzureUserAssignedIdentity("hostmi");
        var sandboxGroup = builder.AddAzureSandboxGroup("hostgroup");

        identity.WithRoleAssignments(sandboxGroup, AzureSandboxGroupBuiltInRole.SandboxGroupDataOwner);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        var roleAssignments = Assert.Single(model.Resources.OfType<AzureRoleAssignmentResource>(), r => r.Name == "hostmi-roles-hostgroup");
        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(roleAssignments, skipPreparer: true);

        await Verify(manifest.ToString(), "json")
            .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public void SandboxRoleDefinitionUsesSandboxGroupDataOwnerRoleId()
    {
        Assert.Equal("c24cf47c-5077-412d-a19c-45202126392c", AzureSandboxGroupBuiltInRole.SandboxGroupDataOwner.ToString());
        Assert.Equal("SandboxGroup Data Owner", AzureSandboxGroupBuiltInRole.GetBuiltInRoleName(AzureSandboxGroupBuiltInRole.SandboxGroupDataOwner));
    }

    [Fact]
    public void WithConnectorAppendsOperationsToExistingConnector()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var gateway = builder.AddAzureConnectorGateway("gateway");
        var connection = gateway.AddConnection("teams", "a365teamsmcp");
        var config = gateway.AddMcpServerConfig("teamsmcp", "Microsoft Teams MCP server");

        config.WithConnector("a365teamsmcp", connection, "mcp_TeamsServer");
        config.WithConnector("a365teamsmcp", connection, "mcp_TeamsChat");

        var connector = Assert.Single(config.Resource.Connectors);
        Assert.Equal("a365teamsmcp", connector.Name);
        Assert.Same(connection.Resource, connector.Connection);
        Assert.Collection(
            connector.Operations,
            operation => Assert.Equal("mcp_TeamsServer", operation.Name),
            operation => Assert.Equal("mcp_TeamsChat", operation.Name));
    }

    [Fact]
    public void ContainerImageMetadataBuildsSandboxEntrypointFromImageConfig()
    {
        var metadata = AzureSandboxContainerDeployment.ParseContainerImageMetadata(
            """
            {
              "Entrypoint": ["dotnet", "/app/yarp.dll"],
              "Cmd": null,
              "WorkingDir": "/app",
              "Env": [
                "PATH=/usr/local/bin:/usr/bin:/bin",
                "ASPNETCORE_URLS=http://+:5000",
                "EMPTY="
              ]
            }
            """,
            "example.azurecr.io/site:tag");

        Assert.Equal(["dotnet", "/app/yarp.dll"], metadata.Entrypoint);
        Assert.Empty(metadata.Command);
        Assert.Equal("/usr/local/bin:/usr/bin:/bin", metadata.EnvironmentVariables["PATH"]);
        Assert.Equal("http://+:5000", metadata.EnvironmentVariables["ASPNETCORE_URLS"]);
        Assert.Equal(string.Empty, metadata.EnvironmentVariables["EMPTY"]);
    }

    [Fact]
    public async Task AzureDevComputeClientCreatesDiskImageWithRegistryCredentials()
    {
        var credential = new RecordingTokenCredential();
        var handler = new RecordingHandler(async request =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("management.westus3.azuredevcompute.io", request.RequestUri?.Host);
            Assert.Equal("/subscriptions/sub/resourceGroups/rg/sandboxGroups/sg/diskimages", request.RequestUri?.AbsolutePath);
            Assert.Equal("?api-version=2026-02-01-preview", request.RequestUri?.Query);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-token", request.Headers.Authorization?.Parameter);

            var body = await request.Content!.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            Assert.Equal("site-1234", root.GetProperty("name").GetString());
            Assert.Equal("site-container", root.GetProperty("labels").GetProperty("aspire-resource").GetString());
            Assert.Equal("example.azurecr.io/site:tag", root.GetProperty("image").GetProperty("base").GetString());
            Assert.Equal("00000000-0000-0000-0000-000000000000", root.GetProperty("registryCredentials").GetProperty("username").GetString());
            Assert.Equal("refresh-token", root.GetProperty("registryCredentials").GetProperty("token").GetString());

            return JsonResponse(
                """
                {
                  "id": "disk-1",
                  "labels": {},
                  "image": { "base": "example.azurecr.io/site:tag" },
                  "status": { "state": "Ready", "createdAt": "2026-06-03T00:00:00Z", "updatedAt": "2026-06-03T00:00:00Z" }
                }
                """);
        });
        var client = new AzureDevComputeClient(new HttpClient(handler), credential, NullLogger.Instance);

        var diskImage = await client.CreateDiskImageAsync(
            new AzureDevComputeResourceScope("sub", "rg", "sg", "westus3"),
            new AzureDevComputeCreateDiskImageRequest
            {
                Name = "site-1234",
                Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["aspire-resource"] = "site-container"
                },
                Image = new AzureDevComputeDiskImageSpec
                {
                    Base = "example.azurecr.io/site:tag"
                },
                RegistryCredentials = new AzureDevComputeRegistryCredentials
                {
                    Username = "00000000-0000-0000-0000-000000000000",
                    Token = "refresh-token"
                }
            },
            CancellationToken.None);

        Assert.Equal("disk-1", diskImage.Id);
        Assert.Equal([AzureDevComputeClient.AuthorizationScope], credential.Scopes);
    }

    [Fact]
    public async Task AzureDevComputeClientCreatesSandboxWithContainerMetadata()
    {
        var handler = new RecordingHandler(async request =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("management.westus3.azuredevcompute.io", request.RequestUri?.Host);
            Assert.Equal("/subscriptions/sub/resourceGroups/rg/sandboxGroups/sg/sandboxes", request.RequestUri?.AbsolutePath);
            Assert.Equal("?api-version=2026-02-01-preview", request.RequestUri?.Query);

            var body = await request.Content!.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            Assert.Equal("disk-1", root.GetProperty("sourcesRef").GetProperty("diskImage").GetProperty("id").GetString());
            Assert.False(root.GetProperty("sourcesRef").GetProperty("diskImage").GetProperty("isPublic").GetBoolean());
            Assert.Equal("1000m", root.GetProperty("resources").GetProperty("cpu").GetString());
            Assert.Equal("2048Mi", root.GetProperty("resources").GetProperty("memory").GetString());
            Assert.Equal("20480Mi", root.GetProperty("resources").GetProperty("disk").GetString());
            Assert.Equal("dotnet", root.GetProperty("entrypoint")[0].GetString());
            Assert.Equal("/app/app.dll", root.GetProperty("entrypoint")[1].GetString());
            Assert.Equal("--urls", root.GetProperty("cmd")[0].GetString());
            Assert.Equal("http://+:5000", root.GetProperty("environment").GetProperty("ASPNETCORE_URLS").GetString());

            return JsonResponse(
                """
                {
                  "id": "sandbox-1",
                  "vmmType": "cloudhypervisor",
                  "sourcesRef": { "diskImage": { "id": "disk-1", "isPublic": false } },
                  "resources": { "cpu": "1000m", "memory": "2048Mi", "disk": "20480Mi" },
                  "ports": []
                }
                """,
                HttpStatusCode.Created);
        });
        var client = new AzureDevComputeClient(new HttpClient(handler), new RecordingTokenCredential(), NullLogger.Instance);

        var sandbox = await client.CreateSandboxAsync(
            new AzureDevComputeResourceScope("sub", "rg", "sg", "westus3"),
            new AzureDevComputeSandboxRequest
            {
                Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["aspire-resource"] = "site-container"
                },
                Entrypoint = ["dotnet", "/app/app.dll"],
                Cmd = ["--urls"],
                Environment = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ASPNETCORE_URLS"] = "http://+:5000"
                },
                SourcesRef = new AzureDevComputeSandboxSource
                {
                    DiskImage = new AzureDevComputeSandboxDiskImageSource
                    {
                        Id = "disk-1",
                        IsPublic = false
                    }
                },
                Resources = new AzureDevComputeSandboxResources()
            },
            CancellationToken.None);

        Assert.Equal("sandbox-1", sandbox.Id);
    }

    [Fact]
    public async Task AzureDevComputeClientAddsAnonymousPort()
    {
        var handler = new RecordingHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("management.westus3.azuredevcompute.io", request.RequestUri?.Host);
            Assert.Equal("/subscriptions/sub/resourceGroups/rg/sandboxGroups/sg/sandboxes/sandbox-1/ports/add", request.RequestUri?.AbsolutePath);
            Assert.Equal("?api-version=2026-02-01-preview", request.RequestUri?.Query);

            var body = await request.Content!.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            Assert.Equal(80, root.GetProperty("port").GetInt32());
            Assert.True(root.GetProperty("auth").GetProperty("anonymous").GetBoolean());
            Assert.Equal("Http", root.GetProperty("protocol").GetString());

            return JsonResponse(
                """
                {
                  "ports": [
                    { "port": 80, "url": "https://sandbox.example.test" }
                  ]
                }
                """);
        });
        var client = new AzureDevComputeClient(new HttpClient(handler), new RecordingTokenCredential(), NullLogger.Instance);

        var ports = await client.AddPortAsync(
            new AzureDevComputeResourceScope("sub", "rg", "sg", "westus3"),
            "sandbox-1",
            new AzureDevComputeAddPortRequest
            {
                Port = 80,
                Auth = new AzureDevComputePortAuthConfig { Anonymous = true }
            },
            CancellationToken.None);

        var port = Assert.Single(ports);
        Assert.Equal(80, port.Port);
        Assert.Equal("https://sandbox.example.test/", port.Url.ToString());
    }

    [Fact]
    public async Task SandboxGroupAddsDeploymentTargetForProject()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        builder.AddProject<TestProject>("frontend", launchProfileName: null)
            .WithHttpEndpoint(targetPort: 5000)
            .WithExternalHttpEndpoints();

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        Assert.Empty(model.Resources.OfType<AzureSandboxContainerResource>());

        var computeResource = Assert.Single(model.GetComputeResources(), resource => resource.Name == "frontend");
        Assert.Same(sandboxGroup.Resource, computeResource.GetComputeEnvironment());

        var deploymentTarget = computeResource.GetDeploymentTargetAnnotation(sandboxGroup.Resource);
        Assert.NotNull(deploymentTarget);
        Assert.Same(sandboxGroup.Resource.ContainerRegistry, deploymentTarget.ContainerRegistry);
        Assert.Same(sandboxGroup.Resource, deploymentTarget.ComputeEnvironment);

        var sandboxContainer = Assert.IsType<AzureSandboxContainerResource>(deploymentTarget.DeploymentTarget);
        Assert.Same(computeResource, sandboxContainer.TargetResource);
        Assert.Same(sandboxGroup.Resource, sandboxContainer.Parent);
        Assert.False(sandboxContainer.AutoSuspend);

        var sandboxEndpoint = Assert.Single(AzureSandboxContainerDeployment.ResolveSandboxEndpoints(sandboxContainer));
        Assert.Equal(5000, sandboxEndpoint.TargetPort);
        Assert.True(sandboxEndpoint.IsExternal);
        Assert.True(sandboxEndpoint.IsHttp);

        var pipelineAnnotation = Assert.Single(sandboxContainer.Annotations.OfType<PipelineStepAnnotation>());
        var steps = (await pipelineAnnotation.CreateStepsAsync(new PipelineStepFactoryContext
        {
            PipelineContext = null!,
            Resource = sandboxContainer
        })).ToList();

        var deployStep = Assert.Single(steps, step => step.Name == "deploy-frontend-sandbox-container");
        Assert.Contains(AzureEnvironmentResource.ProvisionInfrastructureStepName, deployStep.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.DeployPrereq, deployStep.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.Deploy, deployStep.RequiredBySteps);
        Assert.Contains(WellKnownPipelineTags.DeployCompute, deployStep.Tags);

        var pushStep = new PipelineStep
        {
            Name = "push-frontend",
            Resource = computeResource,
            Tags = [WellKnownPipelineTags.PushContainerImage],
            Action = _ => Task.CompletedTask
        };
        steps.Add(pushStep);

        foreach (var annotation in sandboxContainer.Annotations.OfType<PipelineConfigurationAnnotation>())
        {
            await annotation.Callback(new PipelineConfigurationContext
            {
                Services = app.Services,
                Steps = steps,
                Model = model
            });
        }

        Assert.Contains(pushStep.Name, deployStep.DependsOnSteps);

        var destroyStep = Assert.Single(steps, step => step.Name == "destroy-frontend-sandbox-container");
        Assert.Contains(WellKnownPipelineSteps.DestroyPrereq, destroyStep.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.Destroy, destroyStep.RequiredBySteps);

        var environmentSteps = await CreateStepsAsync(app, sandboxGroup.Resource);
        var staleCleanupStep = Assert.Single(environmentSteps, step => step.Name == "destroy-stale-azure-sandboxes-sandboxes");
        Assert.Contains(WellKnownPipelineSteps.DestroyPrereq, staleCleanupStep.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.Destroy, staleCleanupStep.RequiredBySteps);

        var azureDestroyStep = new PipelineStep
        {
            Name = "destroy-azure-sandboxes",
            Action = _ => Task.CompletedTask
        };
        environmentSteps.Add(azureDestroyStep);

        var configurationContext = new PipelineConfigurationContext
        {
            Services = app.Services,
            Steps = environmentSteps,
            Model = model
        };

        foreach (var annotation in sandboxGroup.Resource.Annotations.OfType<PipelineConfigurationAnnotation>())
        {
            await annotation.Callback(configurationContext);
        }

        Assert.Contains(destroyStep.Name, azureDestroyStep.DependsOnSteps);
        Assert.Contains(staleCleanupStep.Name, azureDestroyStep.DependsOnSteps);
    }

    [Fact]
    public async Task SandboxGroupUsesExplicitComputeEnvironmentWhenMultipleEnvironmentsExist()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        builder.AddAzureSandboxGroup("othersandboxes");

        builder.AddProject<TestProject>("frontend", launchProfileName: null)
            .WithHttpEndpoint(targetPort: 5000)
            .WithExternalHttpEndpoints()
            .WithComputeEnvironment(sandboxGroup);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        var computeResource = Assert.Single(model.GetComputeResources(), resource => resource.Name == "frontend");
        Assert.Same(sandboxGroup.Resource, computeResource.GetComputeEnvironment());

        var deploymentTarget = computeResource.GetDeploymentTargetAnnotation(sandboxGroup.Resource);
        Assert.NotNull(deploymentTarget);
        var sandboxContainer = Assert.IsType<AzureSandboxContainerResource>(deploymentTarget.DeploymentTarget);
        var sandboxEndpoint = Assert.Single(AzureSandboxContainerDeployment.ResolveSandboxEndpoints(sandboxContainer));
        Assert.Equal(5000, sandboxEndpoint.TargetPort);
        Assert.True(sandboxEndpoint.IsExternal);
    }

    [Fact]
    public async Task SandboxGroupAddsDeploymentTargetForContainerResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var sandboxGroup = builder.AddAzureSandboxGroup("sandboxes");
        builder.AddContainer("frontend", "mcr.microsoft.com/dotnet/runtime-deps", "10.0")
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints();

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        var computeResource = Assert.Single(model.GetComputeResources(), resource => resource.Name == "frontend");
        var deploymentTarget = computeResource.GetDeploymentTargetAnnotation(sandboxGroup.Resource);
        Assert.NotNull(deploymentTarget);

        var sandboxContainer = Assert.IsType<AzureSandboxContainerResource>(deploymentTarget.DeploymentTarget);
        Assert.Same(computeResource, sandboxContainer.TargetResource);
        var sandboxEndpoint = Assert.Single(AzureSandboxContainerDeployment.ResolveSandboxEndpoints(sandboxContainer));
        Assert.Equal(8080, sandboxEndpoint.TargetPort);
    }

    private static async Task<List<PipelineStep>> CreateStepsAsync(DistributedApplication app, AzureSandboxGroupResource resource)
    {
        var pipelineContext = new PipelineContext(
            app.Services.GetRequiredService<DistributedApplicationModel>(),
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
            app.Services,
            NullLogger.Instance,
            CancellationToken.None);

        var results = new List<PipelineStep>();
        foreach (var annotation in resource.Annotations.OfType<PipelineStepAnnotation>())
        {
            results.AddRange(await annotation.CreateStepsAsync(new PipelineStepFactoryContext
            {
                PipelineContext = pipelineContext,
                Resource = resource
            }));
        }

        return results;
    }

    private sealed class TestProject : IProjectMetadata
    {
        public string ProjectPath => "testproject";
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return handler(request);
        }
    }

    private sealed class RecordingTokenCredential : TokenCredential
    {
        public string[] Scopes { get; private set; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scopes = [.. requestContext.Scopes];
            return new AccessToken("test-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scopes = [.. requestContext.Scopes];
            return ValueTask.FromResult(new AccessToken("test-token", DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    private static HttpResponseMessage JsonResponse(string content, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };
    }
}
