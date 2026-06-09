#pragma warning disable ASPIREAZURE001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Utils;
using Aspire.Hosting.Azure.Provisioning;
using Aspire.Hosting.Azure.Provisioning.Internal;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Tests;
using Azure;
using Azure.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Azure.Tests;

public class AzureEnvironmentResourceExtensionsTests
{
    [Fact]
    public void AddAzureEnvironment_ShouldAddResourceToBuilder_InPublishMode()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        var resourceBuilder = builder.AddAzureEnvironment();

        // Assert
        Assert.NotNull(resourceBuilder);
        var environmentResource = Assert.Single(builder.Resources.OfType<AzureEnvironmentResource>());
        // Assert that default Location and ResourceGroup parameters are set
        Assert.NotNull(environmentResource.Location);
        Assert.NotNull(environmentResource.ResourceGroupName);
        // Assert that the parameters are not added to the resource model
        Assert.Empty(builder.Resources.OfType<ParameterResource>());
    }

    [Fact]
    public void AddAzureEnvironment_CalledMultipleTimes_ReturnsSameResource()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        var firstBuilder = builder.AddAzureEnvironment();
        var secondBuilder = builder.AddAzureEnvironment();

        // Assert
        Assert.Same(firstBuilder.Resource, secondBuilder.Resource);
        Assert.Single(builder.Resources.OfType<AzureEnvironmentResource>());
    }

    [Fact]
    public void AddAzureEnvironment_InRunMode_AddsControlResourceWithResetCommand()
    {
        var builder = CreateBuilder(isRunMode: true);

        var resourceBuilder = builder.AddAzureEnvironment();

        Assert.NotNull(resourceBuilder);
        var environmentResource = Assert.Single(builder.Resources.OfType<AzureEnvironmentResource>());
        var resetCommand = Assert.Single(environmentResource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ResetProvisioningStateCommandName);
        Assert.Equal("Reset provisioning state", resetCommand.DisplayName);
        Assert.Contains("not delete live Azure resources", resetCommand.DisplayDescription);
        Assert.Contains("may be left orphaned", resetCommand.ConfirmationMessage);
    }

    [Fact]
    public void AddAzureEnvironment_InRunMode_AddsCommandsInDefinitionOrder()
    {
        var builder = CreateBuilder(isRunMode: true);

        builder.AddAzureEnvironment();

        var environmentResource = Assert.Single(builder.Resources.OfType<AzureEnvironmentResource>());
        var commands = environmentResource.Annotations.OfType<ResourceCommandAnnotation>().ToArray();

        Assert.Collection(commands,
            command =>
            {
                Assert.Equal(AzureProvisioningController.ResetProvisioningStateCommandName, command.Name);
                Assert.True(command.IsHighlighted);
            },
            command =>
            {
                Assert.Equal(AzureProvisioningController.ChangeAzureContextCommandName, command.Name);
                Assert.True(command.IsHighlighted);
            },
            command =>
            {
                Assert.Equal(AzureProvisioningController.ReprovisionAllCommandName, command.Name);
                Assert.False(command.IsHighlighted);
            },
            command =>
            {
                Assert.Equal(AzureProvisioningController.DeleteAzureResourcesCommandName, command.Name);
                Assert.False(command.IsHighlighted);
            });
    }

    [Fact]
    public void AddAzureEnvironment_InRunMode_AddsSelectableArgumentsToChangeAzureContextCommand()
    {
        var builder = CreateBuilder(isRunMode: true);

        builder.AddAzureEnvironment();

        var environmentResource = Assert.Single(builder.Resources.OfType<AzureEnvironmentResource>());
        var changeContextCommand = Assert.Single(environmentResource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeAzureContextCommandName);

        Assert.NotNull(changeContextCommand.ValidateArguments);
        Assert.Collection(changeContextCommand.Arguments,
            input =>
            {
                Assert.Equal("tenantId", input.Name);
                Assert.True(input.Required);
                Assert.Equal(InputType.Choice, input.InputType);
                Assert.True(input.AllowCustomChoice);
                Assert.NotNull(input.DynamicLoading);
            },
            input =>
            {
                Assert.Equal("subscriptionId", input.Name);
                Assert.True(input.Required);
                Assert.Equal(InputType.Choice, input.InputType);
                Assert.True(input.AllowCustomChoice);
                Assert.True(input.Disabled);
                Assert.NotNull(input.DynamicLoading);
                Assert.Equal(["tenantId"], input.DynamicLoading.DependsOnInputs);
            },
            input =>
            {
                Assert.Equal("resourceGroup", input.Name);
                Assert.True(input.Required);
                Assert.Equal(InputType.Choice, input.InputType);
                Assert.True(input.AllowCustomChoice);
                Assert.NotNull(input.DynamicLoading);
                Assert.Equal(["subscriptionId"], input.DynamicLoading.DependsOnInputs);
            },
            input =>
            {
                Assert.Equal(AzureBicepResource.KnownParameters.Location, input.Name);
                Assert.True(input.Required);
                Assert.Equal(InputType.Choice, input.InputType);
                Assert.True(input.AllowCustomChoice);
                Assert.NotNull(input.DynamicLoading);
                Assert.Equal(["subscriptionId", "resourceGroup"], input.DynamicLoading.DependsOnInputs);
            });
    }

    [Fact]
    public async Task ChangeAzureContextCommand_DynamicArgumentsLoadAzureContextOptions()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();

        builder.Configuration["Azure:TenantId"] = "87654321-4321-4321-4321-210987654321";
        builder.Configuration["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        builder.Configuration["Azure:ResourceGroup"] = "rg-test-2";
        builder.Configuration["Azure:Location"] = "eastus";
        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<IArmClientProvider>();
        builder.Services.RemoveAll<ITokenCredentialProvider>();
        builder.Services.AddSingleton<IArmClientProvider>(ProvisioningTestHelpers.CreateArmClientProvider());
        builder.Services.AddSingleton<ITokenCredentialProvider>(ProvisioningTestHelpers.CreateTokenCredentialProvider());

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var environmentResource = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());
        var changeContextCommand = Assert.Single(environmentResource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeAzureContextCommandName);
        var inputs = CloneInputs(changeContextCommand.Arguments);

        var tenantInput = inputs["tenantId"];
        await tenantInput.DynamicLoading!.LoadCallback(new LoadInputContext
        {
            AllInputs = inputs,
            CancellationToken = CancellationToken.None,
            Input = tenantInput,
            Services = app.Services
        });

        Assert.Contains(tenantInput.Options!, option => option.Key == "87654321-4321-4321-4321-210987654321");
        Assert.Equal("87654321-4321-4321-4321-210987654321", tenantInput.Value);

        var subscriptionInput = inputs["subscriptionId"];
        await subscriptionInput.DynamicLoading!.LoadCallback(new LoadInputContext
        {
            AllInputs = inputs,
            CancellationToken = CancellationToken.None,
            Input = subscriptionInput,
            Services = app.Services
        });

        Assert.False(subscriptionInput.Disabled);
        Assert.Contains(subscriptionInput.Options!, option => option.Key == "12345678-1234-1234-1234-123456789012");
        Assert.Equal("12345678-1234-1234-1234-123456789012", subscriptionInput.Value);

        var resourceGroupInput = inputs["resourceGroup"];
        await resourceGroupInput.DynamicLoading!.LoadCallback(new LoadInputContext
        {
            AllInputs = inputs,
            CancellationToken = CancellationToken.None,
            Input = resourceGroupInput,
            Services = app.Services
        });

        Assert.False(resourceGroupInput.Disabled);
        Assert.Contains(resourceGroupInput.Options!, option => option.Key == "rg-test-2");
        Assert.Equal("rg-test-2", resourceGroupInput.Value);

        var locationInput = inputs[AzureBicepResource.KnownParameters.Location];
        await locationInput.DynamicLoading!.LoadCallback(new LoadInputContext
        {
            AllInputs = inputs,
            CancellationToken = CancellationToken.None,
            Input = locationInput,
            Services = app.Services
        });

        Assert.True(locationInput.Disabled);
        Assert.Equal("westus", locationInput.Value);
        Assert.Equal([KeyValuePair.Create("westus", "westus")], locationInput.Options);
    }

    [Fact]
    public async Task ResetProvisioningStateCommand_ClearsCachedStateAndResetsSnapshots()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var environmentResource = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());

        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        azureSection.Data["SubscriptionId"] = "sub";
        azureSection.Data["Location"] = "westus2";
        await deploymentStateManager.SaveSectionAsync(azureSection);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Id"] = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Resources/deployments/storage";
        storageSection.Data["CheckSum"] = "checksum";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        storage.Resource.Outputs["blobEndpoint"] = "https://storage.blob.core.windows.net/";

        await notifications.PublishUpdateAsync(environmentResource, state => state with
        {
            State = KnownResourceStates.Running,
            Properties =
            [
                new("azure.subscription.id", "sub")
            ]
        });

        await notifications.PublishUpdateAsync(storage.Resource, state => state with
        {
            State = KnownResourceStates.Running,
            Urls =
            [
                new("deployment", "https://portal.azure.com", false)
            ],
            Properties =
            [
                new("azure.subscription.id", "sub"),
                new(CustomResourceKnownProperties.Source, "deployment-id"),
                new("custom.property", "keep")
            ]
        });

        var resetCommand = Assert.Single(environmentResource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ResetProvisioningStateCommandName);

        var result = await resetCommand.ExecuteCommand(new ExecuteCommandContext
        {
            ServiceProvider = app.Services,
            ResourceName = environmentResource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.True(result.Success);
        Assert.Equal("Azure provisioning state reset.", result.Message);

        azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        Assert.Empty(azureSection.Data);

        storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.Empty(storageSection.Data);

        Assert.Empty(storage.Resource.Outputs);

        Assert.True(notifications.TryGetCurrentState(environmentResource.Name, out var environmentEvent));
        Assert.Equal(KnownResourceStates.NotStarted, environmentEvent.Snapshot.State?.Text);
        Assert.All(environmentEvent.Snapshot.Commands, command => Assert.Equal(ResourceCommandState.Enabled, command.State));

        Assert.True(notifications.TryGetCurrentState(storage.Resource.Name, out var storageEvent));
        Assert.Equal(KnownResourceStates.NotStarted, storageEvent.Snapshot.State?.Text);
        Assert.Empty(storageEvent.Snapshot.Urls);
        Assert.DoesNotContain(storageEvent.Snapshot.Properties, p => p.Name == "azure.subscription.id");
        Assert.DoesNotContain(storageEvent.Snapshot.Properties, p => p.Name == CustomResourceKnownProperties.Source);
        Assert.Contains(storageEvent.Snapshot.Properties, p => p.Name == "custom.property");
    }

    [Fact]
    public async Task EnsureProvisionedAsync_UsesControllerProvisioningFlow()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

        var storage = builder.AddAzureStorage("storage");

        using var app = builder.Build();

        var controller = app.Services.GetRequiredService<AzureProvisioningController>();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();

        await controller.EnsureProvisionedAsync(model);

        Assert.True(notifications.TryGetCurrentState(storage.Resource.Name, out var storageEvent));
        Assert.Equal("Running", storageEvent.Snapshot.State?.Text);
        Assert.Equal("https://storage.blob.core.windows.net/", storage.Resource.Outputs["blobEndpoint"]);
    }

    [Fact]
    public async Task RunModePipelineStep_ProvisionsAzureResourcesAfterPrepareStep()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var environmentResource = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());
        var (steps, pipelineContext) = await CreateAzureEnvironmentPipelineStepsAsync(environmentResource, model, app.Services);
        var prepareResourcesStep = Assert.Single(steps, step => step.Name == AzureEnvironmentResource.PrepareResourcesStepName);
        var runModeProvisionStep = Assert.Single(steps, step => step.Name == AzureEnvironmentResource.RunModeProvisionStepName);

        Assert.Contains(AzureEnvironmentResource.PrepareResourcesStepName, runModeProvisionStep.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.BeforeStart, runModeProvisionStep.RequiredBySteps);

        await using var reportingStep = await new NullPublishingActivityReporter().CreateStepAsync("test");
        var stepContext = new PipelineStepContext
        {
            PipelineContext = pipelineContext,
            ReportingStep = reportingStep
        };

        await prepareResourcesStep.Action(stepContext);
        await runModeProvisionStep.Action(stepContext);

        Assert.True(notifications.TryGetCurrentState(storage.Resource.Name, out var storageEvent));
        Assert.Equal(KnownResourceStates.Running, storageEvent.Snapshot.State?.Text);
        Assert.Equal("https://storage.blob.core.windows.net/", storage.Resource.Outputs["blobEndpoint"]);
    }

    [Fact]
    public async Task EnsureProvisioned_UsesCachedStateWhenMissingResourceProbeCannotAuthenticate()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var cachedStateProvisioner = new CachedStateTestBicepProvisioner();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();

        builder.Configuration["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        builder.Configuration["Azure:Location"] = "eastus";
        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.Services.AddSingleton<IArmClientProvider, CredentialUnavailableArmClientProvider>();
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            cachedStateProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Outputs"] = """
            {
              // Cached output IDs are used to decide whether provisioning can be skipped.
              "id": {
                "value": "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/storage",
              },
            }
            """;
        await deploymentStateManager.SaveSectionAsync(storageSection);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var controller = app.Services.GetRequiredService<AzureProvisioningController>();

        await controller.EnsureProvisionedAsync(model);

        Assert.Equal(1, cachedStateProvisioner.ConfigureResourceCallCount);
        Assert.Equal(0, cachedStateProvisioner.GetOrCreateResourceCallCount);
        Assert.Equal("https://storage.blob.core.windows.net/", storage.Resource.Outputs["blobEndpoint"]);
    }

    [Fact]
    public async Task EnsureProvisioned_UsesCachedStateWhenMissingResourceProbeFailsTransiently()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var cachedStateProvisioner = new CachedStateTestBicepProvisioner();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();

        builder.Configuration["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        builder.Configuration["Azure:Location"] = "eastus";
        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.Services.AddSingleton<IArmClientProvider>(new ThrowingResourceProbeArmClientProvider(new RequestFailedException(503, "Service unavailable.")));
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            cachedStateProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Outputs"] = """{"id":{"value":"/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/storage"}}""";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var controller = app.Services.GetRequiredService<AzureProvisioningController>();

        await controller.EnsureProvisionedAsync(model);

        Assert.Equal(1, cachedStateProvisioner.ConfigureResourceCallCount);
        Assert.Equal(0, cachedStateProvisioner.GetOrCreateResourceCallCount);
        Assert.Equal("https://storage.blob.core.windows.net/", storage.Resource.Outputs["blobEndpoint"]);
    }

    [Fact]
    public async Task OnBeforeStartAsync_AddsPerResourceCommandsToDeployableAzureResourcesOnly()
    {
        var builder = CreateBuilder(isRunMode: true);
        builder.AddAzureProvisioning();

        var storage = builder.AddAzureStorage("storage");
        var blobs = storage.AddBlobs("blobs");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        Assert.Contains(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ForgetStateCommandName);
        var changeLocationCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeResourceLocationCommandName);
        var locationArgument = Assert.Single(changeLocationCommand.Arguments);
        Assert.Equal(AzureBicepResource.KnownParameters.Location, locationArgument.Name);
        Assert.True(locationArgument.Required);
        Assert.Contains(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.GetAzureResourceCommandName);
        Assert.Contains(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.CancelDeploymentCommandName);
        Assert.Contains(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.DeleteAzureResourceCommandName);
        Assert.Contains(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ReprovisionResourceCommandName);
        Assert.DoesNotContain(blobs.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c =>
            c.Name == AzureProvisioningController.ForgetStateCommandName ||
            c.Name == AzureProvisioningController.CancelDeploymentCommandName ||
            c.Name == AzureProvisioningController.DeleteAzureResourceCommandName ||
            c.Name == AzureProvisioningController.ReprovisionResourceCommandName);
    }

    [Fact]
    public async Task GetAzureResourceCommand_ReturnsCachedDeploymentStateAndLiveStatus()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        const string subscriptionId = "12345678-1234-1234-1234-123456789012";
        const string tenantId = "87654321-4321-4321-4321-210987654321";
        const string resourceGroup = "test-rg";
        const string resourceId = $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Storage/storageAccounts/storage";
        const string deploymentId = $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Resources/deployments/storage";

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<IArmClientProvider>();
        builder.Services.RemoveAll<ITokenCredentialProvider>();
        builder.Services.AddSingleton<IArmClientProvider>(ProvisioningTestHelpers.CreateArmClientProvider([resourceId]));
        builder.Services.AddSingleton<ITokenCredentialProvider>(ProvisioningTestHelpers.CreateTokenCredentialProvider());

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        azureSection.Data["SubscriptionId"] = subscriptionId;
        azureSection.Data["Location"] = "westus2";
        azureSection.Data["ResourceGroup"] = resourceGroup;
        azureSection.Data["TenantId"] = tenantId;
        await deploymentStateManager.SaveSectionAsync(azureSection);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Id"] = deploymentId;
        storageSection.Data["Parameters"] = """
            {
              // Cached deployment state can be hand-edited while recovering local state.
              "location": { "value": "westus2", },
            }
            """;
        storageSection.Data["Outputs"] = $$"""
            {
              "id": {
                "type": "String",
                "value": "{{resourceId}}",
              },
              "blobEndpoint": {
                "type": "String",
                "value": "https://storage.blob.core.windows.net/",
              },
            }
            """;
        storageSection.Data["Scope"] = $$"""
            {
              "resourceGroup": "{{resourceGroup}}",
            }
            """;
        storageSection.Data["CheckSum"] = "checksum";
        storageSection.Data[BicepProvisioner.DeploymentStateProvisioningStateKey] = BicepProvisioner.DeploymentStateProvisioningStateRunning;
        storageSection.Data[AzureProvisioningController.LocationOverrideKey] = "westus3";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        var getResourceCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.GetAzureResourceCommandName);

        var result = await getResourceCommand.ExecuteCommand(new ExecuteCommandContext
        {
            ServiceProvider = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.True(result.Success);
        Assert.Equal("Azure resource information retrieved.", result.Message);

        var data = AssertCommandJsonData(result);
        Assert.Equal(AzureProvisioningController.GetAzureResourceCommandName, data["command"]?.GetValue<string>());
        Assert.Equal("storage", data["resourceName"]?.GetValue<string>());
        Assert.Equal("westus3", data["location"]?.GetValue<string>());
        Assert.NotNull(result.Data);
        Assert.True(result.Data!.DisplayImmediately);

        var deployment = Assert.IsType<JsonObject>(data["deployment"]);
        Assert.True(deployment["hasState"]?.GetValue<bool>());
        Assert.Equal(deploymentId, deployment["deploymentId"]?.GetValue<string>());
        Assert.Equal(resourceId, deployment["resourceId"]?.GetValue<string>());
        Assert.Equal("westus3", deployment["locationOverride"]?.GetValue<string>());
        Assert.Equal("checksum", deployment["checksum"]?.GetValue<string>());
        Assert.Equal(BicepProvisioner.DeploymentStateProvisioningStateRunning, deployment["provisioningState"]?.GetValue<string>());
        Assert.Contains("/DeploymentDetailsBlade/", deployment["deploymentPortalUrl"]?.GetValue<string>());
        Assert.Contains("/resource/subscriptions/", deployment["resourcePortalUrl"]?.GetValue<string>());

        var outputs = Assert.IsType<JsonObject>(deployment["outputs"]);
        Assert.Equal("https://storage.blob.core.windows.net/", outputs["blobEndpoint"]?["value"]?.GetValue<string>());
        var parameters = Assert.IsType<JsonObject>(deployment["parameters"]);
        Assert.Equal("westus2", parameters["location"]?["value"]?.GetValue<string>());
        var scope = Assert.IsType<JsonObject>(deployment["scope"]);
        Assert.Equal(resourceGroup, scope["resourceGroup"]?.GetValue<string>());

        var live = Assert.IsType<JsonObject>(data["live"]);
        Assert.True(live["checked"]?.GetValue<bool>());
        Assert.True(live["exists"]?.GetValue<bool>());
    }

    [Fact]
    public async Task CancelDeploymentCommand_CancelsCachedDeploymentAndMarksStateCanceled()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var canceledDeploymentIds = new List<string>();
        const string subscriptionId = "12345678-1234-1234-1234-123456789012";
        const string resourceGroup = "test-rg";
        const string deploymentId = $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Resources/deployments/storage";

        builder.Configuration["Azure:SubscriptionId"] = subscriptionId;
        builder.Configuration["Azure:Location"] = "westus2";
        builder.Configuration["Azure:ResourceGroup"] = resourceGroup;
        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<IArmClientProvider>();
        builder.Services.RemoveAll<ITokenCredentialProvider>();
        builder.Services.AddSingleton<IArmClientProvider>(ProvisioningTestHelpers.CreateArmClientProvider(
            existingResourceIds: Array.Empty<string>(),
            deletedResourceIds: null,
            deploymentTargetResourceIds: null,
            canceledDeploymentIds: canceledDeploymentIds));
        builder.Services.AddSingleton<ITokenCredentialProvider>(ProvisioningTestHelpers.CreateTokenCredentialProvider());

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Id"] = deploymentId;
        storageSection.Data[BicepProvisioner.DeploymentStateProvisioningStateKey] = BicepProvisioner.DeploymentStateProvisioningStateRunning;
        await deploymentStateManager.SaveSectionAsync(storageSection);

        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = new("Waiting for Deployment", KnownResourceStateStyles.Info) });

        var cancelCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.CancelDeploymentCommandName);

        var result = await cancelCommand.ExecuteCommand(new ExecuteCommandContext
        {
            ServiceProvider = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.True(result.Success);
        Assert.Equal("Azure deployment cancellation requested.", result.Message);
        Assert.Equal([deploymentId], canceledDeploymentIds);

        storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.Equal(BicepProvisioner.DeploymentStateProvisioningStateCanceled, storageSection.Data[BicepProvisioner.DeploymentStateProvisioningStateKey]?.GetValue<string>());

        Assert.True(notifications.TryGetCurrentState(storage.Resource.Name, out var storageEvent));
        Assert.Equal("Canceled", storageEvent.Snapshot.State?.Text);
    }

    [Fact]
    public async Task CancelDeploymentCommand_IsEnabledDuringActiveDeploymentOperation()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new BlockingTestBicepProvisioner();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();

        builder.Configuration["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        builder.Configuration["Azure:Location"] = "westus2";
        builder.Configuration["Azure:ResourceGroup"] = "test-rg";
        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());
        var controller = app.Services.GetRequiredService<AzureProvisioningController>();

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var provisioningTask = controller.EnsureProvisionedAsync(model, CancellationToken.None);

        await testBicepProvisioner.FirstProvisionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = new("Creating ARM Deployment", KnownResourceStateStyles.Info) });

        Assert.True(notifications.TryGetCurrentState(storage.Resource.Name, out var storageEvent));
        var cancelCommand = Assert.Single(storageEvent.Snapshot.Commands, c => c.Name == AzureProvisioningController.CancelDeploymentCommandName);
        Assert.Equal(ResourceCommandState.Enabled, cancelCommand.State);

        testBicepProvisioner.AllowFirstProvisionToComplete.TrySetResult();
        await provisioningTask;
    }

    [Fact]
    public async Task CancelDeploymentCommand_IsDisabledWhenResourceIsNotWaitingForDeployment()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Id"] = "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Resources/deployments/storage";
        storageSection.Data[BicepProvisioner.DeploymentStateProvisioningStateKey] = BicepProvisioner.DeploymentStateProvisioningStateSucceeded;
        await deploymentStateManager.SaveSectionAsync(storageSection);

        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = KnownResourceStates.Running });

        Assert.True(notifications.TryGetCurrentState(storage.Resource.Name, out var storageEvent));
        var cancelCommand = Assert.Single(storageEvent.Snapshot.Commands, c => c.Name == AzureProvisioningController.CancelDeploymentCommandName);
        Assert.Equal(ResourceCommandState.Disabled, cancelCommand.State);
    }

    [Fact]
    public async Task CancelDeploymentCommand_DoesNotMarkCompletedDeploymentCanceled()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        const string subscriptionId = "12345678-1234-1234-1234-123456789012";
        const string resourceGroup = "test-rg";
        const string deploymentId = $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Resources/deployments/storage";

        builder.Configuration["Azure:SubscriptionId"] = subscriptionId;
        builder.Configuration["Azure:Location"] = "westus2";
        builder.Configuration["Azure:ResourceGroup"] = resourceGroup;
        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<IArmClientProvider>();
        builder.Services.RemoveAll<ITokenCredentialProvider>();
        builder.Services.AddSingleton<IArmClientProvider>(new CancelConflictArmClientProvider());
        builder.Services.AddSingleton<ITokenCredentialProvider>(ProvisioningTestHelpers.CreateTokenCredentialProvider());

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Id"] = deploymentId;
        storageSection.Data[BicepProvisioner.DeploymentStateProvisioningStateKey] = BicepProvisioner.DeploymentStateProvisioningStateSucceeded;
        await deploymentStateManager.SaveSectionAsync(storageSection);

        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = KnownResourceStates.Running });

        var cancelCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.CancelDeploymentCommandName);

        var result = await cancelCommand.ExecuteCommand(new ExecuteCommandContext
        {
            ServiceProvider = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.False(result.Success);
        Assert.False(result.Canceled);

        storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.Equal(BicepProvisioner.DeploymentStateProvisioningStateSucceeded, storageSection.Data[BicepProvisioner.DeploymentStateProvisioningStateKey]?.GetValue<string>());

        Assert.True(notifications.TryGetCurrentState(storage.Resource.Name, out var storageEvent));
        Assert.Equal(KnownResourceStates.Running, storageEvent.Snapshot.State?.Text);
    }

    [Fact]
    public async Task DeleteAzureResourceCommand_DeletesCachedOutputAndDeploymentOperationTargets()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var deletedResourceIds = new List<string>();
        var canceledDeploymentIds = new List<string>();
        const string subscriptionId = "12345678-1234-1234-1234-123456789012";
        const string resourceGroup = "test-rg";
        const string deploymentId = $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Resources/deployments/storage";
        const string storageResourceId = $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Storage/storageAccounts/storage26wmkwq4f4li52";
        const string partialIdentityResourceId = $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/storage-identity";
        const string nestedDeploymentResourceId = $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Resources/deployments/nested";

        builder.Configuration["Azure:SubscriptionId"] = subscriptionId;
        builder.Configuration["Azure:Location"] = "westus2";
        builder.Configuration["Azure:ResourceGroup"] = resourceGroup;
        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<IArmClientProvider>();
        builder.Services.RemoveAll<ITokenCredentialProvider>();
        builder.Services.AddSingleton<IArmClientProvider>(ProvisioningTestHelpers.CreateArmClientProvider(
            existingResourceIds: [storageResourceId, partialIdentityResourceId, nestedDeploymentResourceId],
            deletedResourceIds: deletedResourceIds,
            deploymentTargetResourceIds: [partialIdentityResourceId, nestedDeploymentResourceId],
            canceledDeploymentIds: canceledDeploymentIds));
        builder.Services.AddSingleton<ITokenCredentialProvider>(ProvisioningTestHelpers.CreateTokenCredentialProvider());

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");
        var storage2 = builder.AddBicepTemplateString("storage2", "resource storage2 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Id"] = deploymentId;
        storageSection.Data["Outputs"] = new JsonObject
        {
            ["id"] = new JsonObject
            {
                ["type"] = "String",
                ["value"] = storageResourceId
            }
        }.ToJsonString();
        storageSection.Data[AzureProvisioningController.LocationOverrideKey] = "westus3";
        storageSection.Data[BicepProvisioner.DeploymentStateProvisioningStateKey] = BicepProvisioner.DeploymentStateProvisioningStateRunning;
        await deploymentStateManager.SaveSectionAsync(storageSection);

        var storage2Section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2");
        storage2Section.Data["Id"] = "storage2-deployment";
        await deploymentStateManager.SaveSectionAsync(storage2Section);

        storage.Resource.Outputs["blobEndpoint"] = "https://storage.blob.core.windows.net/";
        storage2.Resource.Outputs["blobEndpoint"] = "https://storage2.blob.core.windows.net/";

        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = new("Failed to Provision", KnownResourceStateStyles.Error) });
        await notifications.PublishUpdateAsync(storage2.Resource, state => state with { State = KnownResourceStates.Running });

        var deleteCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.DeleteAzureResourceCommandName);

        var result = await deleteCommand.ExecuteCommand(new ExecuteCommandContext
        {
            ServiceProvider = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.True(result.Success);
        Assert.Equal("Azure resources deleted and provisioning state reset.", result.Message);
        Assert.Equal([deploymentId], canceledDeploymentIds);
        Assert.Equal(2, deletedResourceIds.Count);
        Assert.Contains(storageResourceId, deletedResourceIds);
        Assert.Contains(partialIdentityResourceId, deletedResourceIds);
        Assert.DoesNotContain(nestedDeploymentResourceId, deletedResourceIds);

        var resultData = AssertCommandJsonData(result);
        Assert.Equal(2, resultData["deletedResourceCount"]?.GetValue<int>());
        var resultResourceIds = Assert.IsType<JsonArray>(resultData["deletedResourceIds"]);
        Assert.Equal(deletedResourceIds, resultResourceIds.Select(static resourceId => resourceId!.GetValue<string>()));

        storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.Equal("westus3", storageSection.Data[AzureProvisioningController.LocationOverrideKey]?.GetValue<string>());
        Assert.False(storageSection.Data.ContainsKey("Id"));
        Assert.False(storageSection.Data.ContainsKey("Outputs"));

        storage2Section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2");
        Assert.Equal("storage2-deployment", storage2Section.Data["Id"]?.GetValue<string>());

        Assert.Empty(storage.Resource.Outputs);
        Assert.Equal("https://storage2.blob.core.windows.net/", storage2.Resource.Outputs["blobEndpoint"]);

        Assert.True(notifications.TryGetCurrentState(storage.Resource.Name, out var storageEvent));
        Assert.Equal(KnownResourceStates.NotStarted, storageEvent.Snapshot.State?.Text);

        Assert.True(notifications.TryGetCurrentState(storage2.Resource.Name, out var storage2Event));
        Assert.Equal(KnownResourceStates.Running, storage2Event.Snapshot.State?.Text);
    }

    [Fact]
    public async Task ForgetStateCommand_ClearsOnlyTargetedResourceStateAndSnapshots()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");
        var storage2 = builder.AddBicepTemplateString("storage2", "resource storage2 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Id"] = "storage-deployment";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        var storage2Section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2");
        storage2Section.Data["Id"] = "storage2-deployment";
        await deploymentStateManager.SaveSectionAsync(storage2Section);

        storage.Resource.Outputs["blobEndpoint"] = "https://storage.blob.core.windows.net/";
        storage2.Resource.Outputs["blobEndpoint"] = "https://storage2.blob.core.windows.net/";

        await notifications.PublishUpdateAsync(storage.Resource, state => state with
        {
            State = KnownResourceStates.Running,
            Urls = [new("deployment", "https://portal.azure.com/storage", false)],
            Properties = [new(CustomResourceKnownProperties.Source, "storage-deployment"), new("custom.property", "keep-storage")]
        });

        await notifications.PublishUpdateAsync(storage2.Resource, state => state with
        {
            State = KnownResourceStates.Running,
            Urls = [new("deployment", "https://portal.azure.com/storage2", false)],
            Properties = [new(CustomResourceKnownProperties.Source, "storage2-deployment"), new("custom.property", "keep-storage2")]
        });

        var forgetCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ForgetStateCommandName);

        var result = await forgetCommand.ExecuteCommand(new ExecuteCommandContext
        {
            ServiceProvider = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.True(result.Success);
        Assert.Equal("Azure resource provisioning state reset.", result.Message);

        storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.Empty(storageSection.Data);

        storage2Section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2");
        Assert.Equal("storage2-deployment", storage2Section.Data["Id"]?.GetValue<string>());

        Assert.Empty(storage.Resource.Outputs);
        Assert.Equal("https://storage2.blob.core.windows.net/", storage2.Resource.Outputs["blobEndpoint"]);

        Assert.True(notifications.TryGetCurrentState(storage.Resource.Name, out var storageEvent));
        Assert.Equal(KnownResourceStates.NotStarted, storageEvent.Snapshot.State?.Text);
        Assert.DoesNotContain(storageEvent.Snapshot.Properties, p => p.Name == CustomResourceKnownProperties.Source);
        Assert.Contains(storageEvent.Snapshot.Properties, p => p.Name == "custom.property");

        Assert.True(notifications.TryGetCurrentState(storage2.Resource.Name, out var storage2Event));
        Assert.Equal(KnownResourceStates.Running, storage2Event.Snapshot.State?.Text);
        Assert.Contains(storage2Event.Snapshot.Properties, p => p.Name == CustomResourceKnownProperties.Source);
    }

    [Fact]
    public async Task ReprovisionCommand_ReprovisionsOnlyTargetedResource()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");
        var storage2 = builder.AddBicepTemplateString("storage2", "resource storage2 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Id"] = "storage-deployment";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        var storage2Section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2");
        storage2Section.Data["Id"] = "storage2-deployment";
        await deploymentStateManager.SaveSectionAsync(storage2Section);

        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = KnownResourceStates.Running });
        await notifications.PublishUpdateAsync(storage2.Resource, state => state with { State = KnownResourceStates.Running });

        var reprovisionCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ReprovisionResourceCommandName);

        var result = await reprovisionCommand.ExecuteCommand(new ExecuteCommandContext
        {
            ServiceProvider = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.True(result.Success);
        Assert.Equal("Azure resource reprovisioning completed.", result.Message);
        Assert.Contains(storage.Resource.Outputs, output => output.Key == "blobEndpoint");

        storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.Empty(storageSection.Data);

        storage2Section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2");
        Assert.Equal("storage2-deployment", storage2Section.Data["Id"]?.GetValue<string>());

        Assert.True(notifications.TryGetCurrentState(storage.Resource.Name, out var storageEvent));
        Assert.Equal("Running", storageEvent.Snapshot.State?.Text);

        Assert.True(notifications.TryGetCurrentState(storage2.Resource.Name, out var storage2Event));
        Assert.Equal(KnownResourceStates.Running, storage2Event.Snapshot.State?.Text);
    }

    [Fact]
    public async Task ChangeLocationCommand_PersistsOverrideAndReprovisionsTargetedResource()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();
        var testInteractionService = new TestInteractionService();

        builder.Configuration["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        builder.Configuration["Azure:Location"] = "eastus";
        builder.Services.AddSingleton<IInteractionService>(testInteractionService);
        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");
        var storage2 = builder.AddBicepTemplateString("storage2", "resource storage2 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = new("Failed to Provision", KnownResourceStateStyles.Error) });
        await notifications.PublishUpdateAsync(storage2.Resource, state => state with { State = KnownResourceStates.Running });

        var changeLocationCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeResourceLocationCommandName);

        var executionTask = changeLocationCommand.ExecuteCommand(new ExecuteCommandContext
        {
            ServiceProvider = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        var interaction = await testInteractionService.Interactions.Reader.ReadAsync();
        interaction.Inputs[AzureBicepResource.KnownParameters.Location].Value = "westus2";
        interaction.CompletionTcs.SetResult(InteractionResult.Ok(interaction.Inputs));

        var result = await executionTask;

        Assert.True(result.Success);
        Assert.Equal("Azure resource location updated and reprovisioning completed.", result.Message);
        Assert.Equal("westus2", testBicepProvisioner.ProvisionedLocations["storage"]);
        Assert.DoesNotContain("storage2", testBicepProvisioner.ProvisionedLocations.Keys);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.Equal("westus2", storageSection.Data[AzureProvisioningController.LocationOverrideKey]?.GetValue<string>());
    }

    [Fact]
    public async Task ChangeLocationCommand_WithArguments_DoesNotPromptAndReturnsJsonResult()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();
        var testInteractionService = new TestInteractionService();

        builder.Configuration["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        builder.Configuration["Azure:Location"] = "eastus";
        builder.Configuration["Azure:ResourceGroup"] = "test-rg";
        builder.Services.AddSingleton<IInteractionService>(testInteractionService);
        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<IArmClientProvider>();
        builder.Services.RemoveAll<ITokenCredentialProvider>();
        builder.Services.AddSingleton<IArmClientProvider>(ProvisioningTestHelpers.CreateArmClientProvider());
        builder.Services.AddSingleton<ITokenCredentialProvider>(ProvisioningTestHelpers.CreateTokenCredentialProvider());
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);
        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = KnownResourceStates.Running });

        var changeLocationCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeResourceLocationCommandName);

        var result = await changeLocationCommand.ExecuteCommand(new ExecuteCommandContext
        {
            ServiceProvider = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = CreateArguments((AzureBicepResource.KnownParameters.Location, "West US 3"))
        });

        Assert.True(result.Success);
        Assert.Equal("Azure resource location updated and reprovisioning completed.", result.Message);
        Assert.Equal("westus3", testBicepProvisioner.ProvisionedLocations["storage"]);
        Assert.False(testInteractionService.Interactions.Reader.TryRead(out _));

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.Equal("westus3", storageSection.Data[AzureProvisioningController.LocationOverrideKey]?.GetValue<string>());

        var data = AssertCommandJsonData(result);
        Assert.Equal(1, data["schemaVersion"]?.GetValue<int>());
        Assert.Equal(AzureProvisioningController.ChangeResourceLocationCommandName, data["command"]?.GetValue<string>());
        Assert.Equal("storage", data["resourceName"]?.GetValue<string>());
        Assert.Equal("westus3", data["location"]?.GetValue<string>());
        Assert.Equal("eastus", data["azureLocation"]?.GetValue<string>());
    }

    [Fact]
    public async Task ChangeLocationCommand_ForAnnotatedResource_PersistsOverrideUnderBicepResourceName()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();
        var testInteractionService = new TestInteractionService();

        builder.Configuration["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        builder.Configuration["Azure:Location"] = "eastus";
        builder.Configuration["Azure:ResourceGroup"] = "test-rg";
        builder.Services.AddSingleton<IInteractionService>(testInteractionService);
        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<IArmClientProvider>();
        builder.Services.RemoveAll<ITokenCredentialProvider>();
        builder.Services.AddSingleton<IArmClientProvider>(ProvisioningTestHelpers.CreateArmClientProvider());
        builder.Services.AddSingleton<ITokenCredentialProvider>(ProvisioningTestHelpers.CreateTokenCredentialProvider());
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

        var visibleResource = new AnnotatedAzureResource("storage");
        var bicepResource = new AzureBicepResource("storage-deployment", templateString: "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");
        visibleResource.Annotations.Add(new AzureBicepResourceAnnotation(bicepResource));
        builder.AddResource(visibleResource);

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);
        await notifications.PublishUpdateAsync(visibleResource, state => state with { State = KnownResourceStates.Running });

        var changeLocationCommand = Assert.Single(visibleResource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeResourceLocationCommandName);

        var result = await changeLocationCommand.ExecuteCommand(new ExecuteCommandContext
        {
            ServiceProvider = app.Services,
            ResourceName = visibleResource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = CreateArguments((AzureBicepResource.KnownParameters.Location, "West US 3"))
        });

        Assert.True(result.Success);
        Assert.Equal("westus3", testBicepProvisioner.ProvisionedLocations["storage-deployment"]);

        var bicepSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage-deployment");
        Assert.Equal("westus3", bicepSection.Data[AzureProvisioningController.LocationOverrideKey]?.GetValue<string>());

        var visibleSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.False(visibleSection.Data.ContainsKey(AzureProvisioningController.LocationOverrideKey));
    }

    [Fact]
    public async Task ChangeLocationCommand_UsesPersistedAzureContextForSelectableLocations()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();
        var testInteractionService = new TestInteractionService();

        builder.Services.AddSingleton<IInteractionService>(testInteractionService);
        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<IArmClientProvider>();
        builder.Services.RemoveAll<ITokenCredentialProvider>();
        builder.Services.AddSingleton<IArmClientProvider>(ProvisioningTestHelpers.CreateArmClientProvider());
        builder.Services.AddSingleton<ITokenCredentialProvider>(ProvisioningTestHelpers.CreateTokenCredentialProvider());
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        azureSection.Data["SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        azureSection.Data["Location"] = "eastus";
        azureSection.Data["ResourceGroup"] = "test-rg";
        await deploymentStateManager.SaveSectionAsync(azureSection);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data[AzureProvisioningController.LocationOverrideKey] = "westus3";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = KnownResourceStates.Running });

        var changeLocationCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeResourceLocationCommandName);
        var commandInputs = CloneInputs(changeLocationCommand.Arguments);
        var commandLocationInput = commandInputs[AzureBicepResource.KnownParameters.Location];

        await commandLocationInput.DynamicLoading!.LoadCallback(new LoadInputContext
        {
            AllInputs = commandInputs,
            CancellationToken = CancellationToken.None,
            Input = commandLocationInput,
            Services = app.Services
        });

        Assert.Equal("westus3", commandLocationInput.Value);

        var executionTask = changeLocationCommand.ExecuteCommand(new ExecuteCommandContext
        {
            ServiceProvider = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        var interaction = await testInteractionService.Interactions.Reader.ReadAsync();
        var locationInput = interaction.Inputs[AzureBicepResource.KnownParameters.Location];

        Assert.Equal(InputType.Choice, locationInput.InputType);
        var options = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, string>>>(locationInput.Options);
        Assert.Contains(options, option => option.Key == "westus2");
        Assert.Equal("westus3", locationInput.Value);

        locationInput.Value = "westus2";
        interaction.CompletionTcs.SetResult(InteractionResult.Ok(interaction.Inputs));

        var result = await executionTask;

        Assert.True(result.Success);
        Assert.Equal("Azure resource location updated and reprovisioning completed.", result.Message);
    }

    [Fact]
    public async Task ChangeLocationCommand_DeletesCachedResourceBeforeReprovisioningNewLocation()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();
        var testInteractionService = new TestInteractionService();
        var deletedResourceIds = new List<string>();
        const string resourceId = "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/storage26wmkwq4f4li52";

        builder.Configuration["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        builder.Configuration["Azure:Location"] = "westus2";
        builder.Configuration["Azure:ResourceGroup"] = "test-rg";
        builder.Services.AddSingleton<IInteractionService>(testInteractionService);
        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<IArmClientProvider>();
        builder.Services.RemoveAll<ITokenCredentialProvider>();
        builder.Services.AddSingleton<IArmClientProvider>(ProvisioningTestHelpers.CreateArmClientProvider([resourceId], deletedResourceIds));
        builder.Services.AddSingleton<ITokenCredentialProvider>(ProvisioningTestHelpers.CreateTokenCredentialProvider());
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Outputs"] = """{"id":{"value":"/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/storage26wmkwq4f4li52"}}""";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        await notifications.PublishUpdateAsync(storage.Resource, state => state with
        {
            State = KnownResourceStates.Running,
            Properties = [new("azure.location", "westus2")]
        });

        var changeLocationCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeResourceLocationCommandName);

        var executionTask = changeLocationCommand.ExecuteCommand(new ExecuteCommandContext
        {
            ServiceProvider = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        var interaction = await testInteractionService.Interactions.Reader.ReadAsync();
        interaction.Inputs[AzureBicepResource.KnownParameters.Location].Value = "westus3";
        interaction.CompletionTcs.SetResult(InteractionResult.Ok(interaction.Inputs));

        var result = await executionTask;

        Assert.True(result.Success);
        Assert.Equal([resourceId], deletedResourceIds);
        Assert.Equal("westus3", testBicepProvisioner.ProvisionedLocations["storage"]);
    }

    [Fact]
    public async Task ChangeLocationCommand_DeletesCachedResourceUsingPersistedLocationWhenSnapshotLocationIsMissing()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();
        var deletedResourceIds = new List<string>();
        const string resourceId = "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/storage26wmkwq4f4li52";

        builder.Configuration["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        builder.Configuration["Azure:Location"] = "westus2";
        builder.Configuration["Azure:ResourceGroup"] = "test-rg";
        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<IArmClientProvider>();
        builder.Services.RemoveAll<ITokenCredentialProvider>();
        builder.Services.AddSingleton<IArmClientProvider>(ProvisioningTestHelpers.CreateArmClientProvider([resourceId], deletedResourceIds));
        builder.Services.AddSingleton<ITokenCredentialProvider>(ProvisioningTestHelpers.CreateTokenCredentialProvider());
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Parameters"] = """
            {
              // The cached deployment location is used when the resource snapshot has not been published yet.
              "location": {
                "value": "westus2",
              },
            }
            """;
        storageSection.Data["Outputs"] = $$"""
            {
              "id": {
                "value": "{{resourceId}}",
              },
            }
            """;
        await deploymentStateManager.SaveSectionAsync(storageSection);

        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = KnownResourceStates.Running });

        var changeLocationCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeResourceLocationCommandName);

        var result = await changeLocationCommand.ExecuteCommand(new ExecuteCommandContext
        {
            ServiceProvider = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = CreateArguments((AzureBicepResource.KnownParameters.Location, "westus3"))
        });

        Assert.True(result.Success);
        Assert.Equal([resourceId], deletedResourceIds);
        Assert.Equal("westus3", testBicepProvisioner.ProvisionedLocations["storage"]);
    }

    [Fact]
    public async Task ChangeLocationCommand_UsesRequestedLocationWhenChangingExistingOverride()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();
        var testInteractionService = new TestInteractionService();
        var deletedResourceIds = new List<string>();
        const string resourceId = "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/storage26wmkwq4f4li52";

        builder.Configuration["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        builder.Configuration["Azure:Location"] = "eastus";
        builder.Configuration["Azure:ResourceGroup"] = "test-rg";
        builder.Services.AddSingleton<IInteractionService>(testInteractionService);
        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<IArmClientProvider>();
        builder.Services.RemoveAll<ITokenCredentialProvider>();
        builder.Services.AddSingleton<IArmClientProvider>(ProvisioningTestHelpers.CreateArmClientProvider([resourceId], deletedResourceIds));
        builder.Services.AddSingleton<ITokenCredentialProvider>(ProvisioningTestHelpers.CreateTokenCredentialProvider());
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["LocationOverride"] = "westus2";
        storageSection.Data["Outputs"] = """{"id":{"value":"/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/storage26wmkwq4f4li52"}}""";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        await notifications.PublishUpdateAsync(storage.Resource, state => state with
        {
            State = KnownResourceStates.Running,
            Properties = [new("azure.location", "westus2")]
        });

        var changeLocationCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeResourceLocationCommandName);

        var executionTask = changeLocationCommand.ExecuteCommand(new ExecuteCommandContext
        {
            ServiceProvider = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        var interaction = await testInteractionService.Interactions.Reader.ReadAsync();
        interaction.Inputs[AzureBicepResource.KnownParameters.Location].Value = "westus3";
        interaction.CompletionTcs.SetResult(InteractionResult.Ok(interaction.Inputs));

        var result = await executionTask;

        Assert.True(result.Success);
        Assert.Equal([resourceId], deletedResourceIds);
        Assert.Equal("westus3", testBicepProvisioner.ProvisionedLocations["storage"]);

        storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.Equal("westus3", storageSection.Data[AzureProvisioningController.LocationOverrideKey]?.GetValue<string>());
    }

    [Fact]
    public async Task ChangeLocationCommand_TreatsDeletedCachedResourceAsAlreadyAbsent()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();
        var testInteractionService = new TestInteractionService();
        const string resourceId = "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/storage26wmkwq4f4li52";

        builder.Configuration["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        builder.Configuration["Azure:Location"] = "westus2";
        builder.Configuration["Azure:ResourceGroup"] = "test-rg";
        builder.Services.AddSingleton<IInteractionService>(testInteractionService);
        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<IArmClientProvider>();
        builder.Services.RemoveAll<ITokenCredentialProvider>();
        builder.Services.AddSingleton<IArmClientProvider>(new DeleteResourceFailureArmClientProvider(resourceId, new RequestFailedException(404, "Not found.")));
        builder.Services.AddSingleton<ITokenCredentialProvider>(ProvisioningTestHelpers.CreateTokenCredentialProvider());
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Outputs"] = """{"id":{"value":"/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/storage26wmkwq4f4li52"}}""";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        await notifications.PublishUpdateAsync(storage.Resource, state => state with
        {
            State = KnownResourceStates.Running,
            Properties = [new("azure.location", "westus2")]
        });

        var changeLocationCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeResourceLocationCommandName);

        var executionTask = changeLocationCommand.ExecuteCommand(new ExecuteCommandContext
        {
            ServiceProvider = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        var interaction = await testInteractionService.Interactions.Reader.ReadAsync();
        interaction.Inputs[AzureBicepResource.KnownParameters.Location].Value = "westus3";
        interaction.CompletionTcs.SetResult(InteractionResult.Ok(interaction.Inputs));

        var result = await executionTask;

        Assert.True(result.Success);
        Assert.Equal("westus3", testBicepProvisioner.ProvisionedLocations["storage"]);

        storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.Equal("westus3", storageSection.Data[AzureProvisioningController.LocationOverrideKey]?.GetValue<string>());
    }

    [Fact]
    public async Task ReprovisionAllCommand_PreservesAzureContextState()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");
        var storage2 = builder.AddBicepTemplateString("storage2", "resource storage2 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var environmentResource = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());

        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        azureSection.Data["SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        azureSection.Data["Location"] = "westus2";
        azureSection.Data["ResourceGroup"] = "test-rg";
        azureSection.Data["TenantId"] = "87654321-4321-4321-4321-210987654321";
        await deploymentStateManager.SaveSectionAsync(azureSection);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Id"] = "storage-deployment";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        var storage2Section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2");
        storage2Section.Data["Id"] = "storage2-deployment";
        await deploymentStateManager.SaveSectionAsync(storage2Section);

        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = KnownResourceStates.Running });
        await notifications.PublishUpdateAsync(storage2.Resource, state => state with { State = KnownResourceStates.Running });

        var reprovisionAllCommand = Assert.Single(environmentResource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ReprovisionAllCommandName);

        var result = await reprovisionAllCommand.ExecuteCommand(new ExecuteCommandContext
        {
            ServiceProvider = app.Services,
            ResourceName = environmentResource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.True(result.Success);
        Assert.Equal("Azure reprovisioning completed.", result.Message);

        azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        Assert.Equal("12345678-1234-1234-1234-123456789012", azureSection.Data["SubscriptionId"]?.GetValue<string>());
        Assert.Equal("westus2", azureSection.Data["Location"]?.GetValue<string>());
        Assert.Equal("test-rg", azureSection.Data["ResourceGroup"]?.GetValue<string>());
        Assert.Equal("87654321-4321-4321-4321-210987654321", azureSection.Data["TenantId"]?.GetValue<string>());
    }

    [Fact]
    public async Task ChangeAzureContextCommand_WithArguments_PersistsContextAndReturnsJsonResult()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();
        var testInteractionService = new TestInteractionService();

        builder.Services.AddSingleton<IInteractionService>(testInteractionService);
        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<IArmClientProvider>();
        builder.Services.RemoveAll<ITokenCredentialProvider>();
        builder.Services.AddSingleton<IArmClientProvider>(ProvisioningTestHelpers.CreateArmClientProvider());
        builder.Services.AddSingleton<ITokenCredentialProvider>(ProvisioningTestHelpers.CreateTokenCredentialProvider());
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var environmentResource = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());

        var changeContextCommand = Assert.Single(environmentResource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeAzureContextCommandName);

        var result = await changeContextCommand.ExecuteCommand(new ExecuteCommandContext
        {
            ServiceProvider = app.Services,
            ResourceName = environmentResource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = CreateArguments(
                ("subscriptionId", "12345678-1234-1234-1234-123456789012"),
                ("resourceGroup", "cli-rg-é"),
                (AzureBicepResource.KnownParameters.Location, "West US 3"),
                ("tenantId", "87654321-4321-4321-4321-210987654321"))
        });

        Assert.True(result.Success);
        Assert.Equal("Azure context updated and resources reprovisioned.", result.Message);
        Assert.Contains("storage", testBicepProvisioner.ProvisionedResources);
        Assert.False(testInteractionService.Interactions.Reader.TryRead(out _));

        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        Assert.Equal("12345678-1234-1234-1234-123456789012", azureSection.Data["SubscriptionId"]?.GetValue<string>());
        Assert.Equal("westus3", azureSection.Data["Location"]?.GetValue<string>());
        Assert.Equal("cli-rg-é", azureSection.Data["ResourceGroup"]?.GetValue<string>());
        Assert.Equal("87654321-4321-4321-4321-210987654321", azureSection.Data["TenantId"]?.GetValue<string>());

        var data = AssertCommandJsonData(result);
        Assert.Equal(1, data["schemaVersion"]?.GetValue<int>());
        Assert.Equal(AzureProvisioningController.ChangeAzureContextCommandName, data["command"]?.GetValue<string>());
        Assert.Equal("12345678-1234-1234-1234-123456789012", data["subscriptionId"]?.GetValue<string>());
        Assert.Equal("87654321-4321-4321-4321-210987654321", data["tenantId"]?.GetValue<string>());
        Assert.Equal("cli-rg-é", data["resourceGroup"]?.GetValue<string>());
        Assert.Equal("westus3", data["azureLocation"]?.GetValue<string>());
        Assert.Contains("cli-rg-é", result.Data!.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u00E9", result.Data.Value, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(1, data["resourceCount"]?.GetValue<int>());

        Assert.True(notifications.TryGetCurrentState(environmentResource.Name, out var environmentEvent));
        Assert.Equal(KnownResourceStates.Running, environmentEvent.Snapshot.State?.Text);
        Assert.Equal("12345678-1234-1234-1234-123456789012", environmentEvent.Snapshot.Properties.Single(p => p.Name == "azure.subscription.id").Value?.ToString());
        Assert.Equal("cli-rg-é", environmentEvent.Snapshot.Properties.Single(p => p.Name == "azure.resource.group").Value?.ToString());
        Assert.Equal("westus3", environmentEvent.Snapshot.Properties.Single(p => p.Name == "azure.location").Value?.ToString());
        Assert.Equal("87654321-4321-4321-4321-210987654321", environmentEvent.Snapshot.Properties.Single(p => p.Name == "azure.tenant.id").Value?.ToString());
    }

    [Fact]
    public async Task ChangeAzureContextCommand_WithArgumentsWithoutTenant_ClearsPersistedTenant()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<IArmClientProvider>();
        builder.Services.RemoveAll<ITokenCredentialProvider>();
        builder.Services.AddSingleton<IArmClientProvider>(ProvisioningTestHelpers.CreateArmClientProvider());
        builder.Services.AddSingleton<ITokenCredentialProvider>(ProvisioningTestHelpers.CreateTokenCredentialProvider());
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

        builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var environmentResource = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());

        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        azureSection.Data["SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        azureSection.Data["Location"] = "westus2";
        azureSection.Data["ResourceGroup"] = "test-rg";
        azureSection.Data["TenantId"] = "87654321-4321-4321-4321-210987654321";
        await deploymentStateManager.SaveSectionAsync(azureSection);

        var changeContextCommand = Assert.Single(environmentResource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeAzureContextCommandName);

        var result = await changeContextCommand.ExecuteCommand(new ExecuteCommandContext
        {
            ServiceProvider = app.Services,
            ResourceName = environmentResource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = CreateArguments(
                ("subscriptionId", "12345678-1234-1234-1234-123456789012"),
                ("resourceGroup", "cli-rg"),
                (AzureBicepResource.KnownParameters.Location, "West US 3"))
        });

        Assert.True(result.Success, result.Message);

        azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        Assert.False(azureSection.Data.ContainsKey("TenantId"));

        var data = AssertCommandJsonData(result);
        Assert.Null(data["tenantId"]);

        Assert.True(notifications.TryGetCurrentState(environmentResource.Name, out var environmentEvent));
        Assert.DoesNotContain(environmentEvent.Snapshot.Properties, p => p.Name == "azure.tenant.id");
    }

    [Fact]
    public async Task ChangeAzureContextCommand_DoesNotInferResourceLocationOverrides()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<IArmClientProvider>();
        builder.Services.RemoveAll<ITokenCredentialProvider>();
        builder.Services.AddSingleton<IArmClientProvider>(ProvisioningTestHelpers.CreateArmClientProvider());
        builder.Services.AddSingleton<ITokenCredentialProvider>(ProvisioningTestHelpers.CreateTokenCredentialProvider());
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var environmentResource = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());

        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        azureSection.Data["SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        azureSection.Data["Location"] = "westus2";
        azureSection.Data["ResourceGroup"] = "test-rg";
        await deploymentStateManager.SaveSectionAsync(azureSection);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Parameters"] = """{"location":{"value":"westus2"}}""";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        await notifications.PublishUpdateAsync(storage.Resource, state => state with
        {
            State = KnownResourceStates.Running,
            Properties = [new("azure.location", "westus2")]
        });

        var changeContextCommand = Assert.Single(environmentResource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeAzureContextCommandName);

        var result = await changeContextCommand.ExecuteCommand(new ExecuteCommandContext
        {
            ServiceProvider = app.Services,
            ResourceName = environmentResource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = CreateArguments(
                ("subscriptionId", "12345678-1234-1234-1234-123456789012"),
                ("resourceGroup", "cli-rg"),
                (AzureBicepResource.KnownParameters.Location, "West US 3"))
        });

        Assert.True(result.Success, result.Message);
        Assert.NotEqual("westus2", testBicepProvisioner.ProvisionedLocations["storage"]);

        storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.False(storageSection.Data.ContainsKey(AzureProvisioningController.LocationOverrideKey));
    }

    [Fact]
    public async Task ReprovisionAllCommand_NormalizesPersistedLocationOverride()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.Services.AddSingleton<IArmClientProvider>(ProvisioningTestHelpers.CreateArmClientProvider());
        builder.Services.AddSingleton<ITokenCredentialProvider>(ProvisioningTestHelpers.CreateTokenCredentialProvider());
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

        builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");
        builder.AddBicepTemplateString("storage2", "resource storage2 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var controller = app.Services.GetRequiredService<AzureProvisioningController>();

        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        azureSection.Data["SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        azureSection.Data["Location"] = "westus2";
        azureSection.Data["ResourceGroup"] = "test-rg";
        await deploymentStateManager.SaveSectionAsync(azureSection);

        var storage2Section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2");
        storage2Section.Data[AzureProvisioningController.LocationOverrideKey] = "West US 3";
        await deploymentStateManager.SaveSectionAsync(storage2Section);

        await controller.ReprovisionAllAsync(model);

        Assert.Equal("westus3", testBicepProvisioner.ProvisionedLocations["storage2"]);

        storage2Section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2");
        Assert.Equal("westus3", storage2Section.Data[AzureProvisioningController.LocationOverrideKey]?.GetValue<string>());
    }

    [Fact]
    public async Task ReprovisionAllCommand_PreservesLocationOverrideFromPersistedParameters()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.Services.AddSingleton<IArmClientProvider>(ProvisioningTestHelpers.CreateArmClientProvider());
        builder.Services.AddSingleton<ITokenCredentialProvider>(ProvisioningTestHelpers.CreateTokenCredentialProvider());
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

        builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");
        builder.AddBicepTemplateString("storage2", "resource storage2 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var controller = app.Services.GetRequiredService<AzureProvisioningController>();

        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        azureSection.Data["SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        azureSection.Data["Location"] = "westus2";
        azureSection.Data["ResourceGroup"] = "test-rg";
        await deploymentStateManager.SaveSectionAsync(azureSection);

        var storage2Section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2");
        storage2Section.Data["Parameters"] = """
            {
              // Preserve resource-specific locations from cached deployment parameters.
              "location": { "value": "westus3", },
            }
            """;
        await deploymentStateManager.SaveSectionAsync(storage2Section);

        await controller.ReprovisionAllAsync(model);

        Assert.Equal("westus3", testBicepProvisioner.ProvisionedLocations["storage2"]);

        storage2Section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2");
        Assert.Equal("westus3", storage2Section.Data[AzureProvisioningController.LocationOverrideKey]?.GetValue<string>());
    }

    [Fact]
    public async Task ReprovisionResourceCommand_PreservesInMemoryLocationOverrideWhenCachedStateIsMissing()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.Services.AddSingleton<IArmClientProvider>(ProvisioningTestHelpers.CreateArmClientProvider());
        builder.Services.AddSingleton<ITokenCredentialProvider>(ProvisioningTestHelpers.CreateTokenCredentialProvider());
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

        builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");
        builder.AddBicepTemplateString("storage2", "resource storage2 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        azureSection.Data["SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        azureSection.Data["Location"] = "westus2";
        azureSection.Data["ResourceGroup"] = "test-rg";
        await deploymentStateManager.SaveSectionAsync(azureSection);

        var storage2 = model.Resources.OfType<AzureBicepResource>().Single(r => r.Name == "storage2");
        await notifications.PublishUpdateAsync(storage2, state => state with
        {
            State = KnownResourceStates.Running,
            Properties =
            [
                new("azure.location", "westus3"),
                new("azure.subscription.id", "12345678-1234-1234-1234-123456789012")
            ]
        });

        var reprovisionCommand = Assert.Single(storage2.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ReprovisionResourceCommandName);

        var result = await reprovisionCommand.ExecuteCommand(new ExecuteCommandContext
        {
            ServiceProvider = app.Services,
            ResourceName = storage2.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.True(result.Success);
        Assert.Equal("Azure resource reprovisioning completed.", result.Message);

        Assert.Equal("westus3", testBicepProvisioner.ProvisionedLocations["storage2"]);

        var storage2Section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2");
        Assert.Equal("westus3", storage2Section.Data[AzureProvisioningController.LocationOverrideKey]?.GetValue<string>());
    }

    [Fact]
    public async Task ForgetResourceStateCommand_ClearsInMemoryLocationParameter()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.Services.AddSingleton<IArmClientProvider>(ProvisioningTestHelpers.CreateArmClientProvider());
        builder.Services.AddSingleton<ITokenCredentialProvider>(ProvisioningTestHelpers.CreateTokenCredentialProvider());
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

        builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var storage = model.Resources.OfType<AzureBicepResource>().Single(r => r.Name == "storage");
        storage.Parameters[AzureBicepResource.KnownParameters.Location] = "westus3";

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Id"] = "storage-deployment";
        storageSection.Data[AzureProvisioningController.LocationOverrideKey] = "westus3";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        var controller = app.Services.GetRequiredService<AzureProvisioningController>();

        await controller.ForgetResourceStateAsync(model, "storage");

        Assert.False(storage.Parameters.ContainsKey(AzureBicepResource.KnownParameters.Location));

        storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.Empty(storageSection.Data);
    }

    [Fact]
    public async Task ReprovisionResourceCommand_FailsWhenProvisioningFails()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            new ThrowingTestBicepProvisioner(),
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = KnownResourceStates.Running });

        var reprovisionCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ReprovisionResourceCommandName);

        var result = await reprovisionCommand.ExecuteCommand(new ExecuteCommandContext
        {
            ServiceProvider = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.False(result.Success);
        Assert.False(result.Canceled);
    }

    [Fact]
    public async Task ChangeLocationCommand_FailsWhenProvisioningFails()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();
        var testInteractionService = new TestInteractionService();

        builder.Configuration["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        builder.Configuration["Azure:Location"] = "eastus";
        builder.Services.AddSingleton<IInteractionService>(testInteractionService);
        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            new ThrowingTestBicepProvisioner(),
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = KnownResourceStates.Running });

        var changeLocationCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeResourceLocationCommandName);

        var executionTask = changeLocationCommand.ExecuteCommand(new ExecuteCommandContext
        {
            ServiceProvider = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        var interaction = await testInteractionService.Interactions.Reader.ReadAsync();
        interaction.Inputs[AzureBicepResource.KnownParameters.Location].Value = "westus2";
        interaction.CompletionTcs.SetResult(InteractionResult.Ok(interaction.Inputs));

        var result = await executionTask;

        Assert.False(result.Success);
        Assert.False(result.Canceled);
    }

    [Fact]
    public async Task ResourceCommandCancellation_ReturnsCanceledResult()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var reprovisionCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ReprovisionResourceCommandName);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await reprovisionCommand.ExecuteCommand(new ExecuteCommandContext
        {
            ServiceProvider = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = cts.Token,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.False(result.Success);
        Assert.True(result.Canceled);
    }

    [Fact]
    public async Task CheckForDriftAsync_MarksResourceMissingInAzure()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.Services.AddSingleton<IArmClientProvider>(ProvisioningTestHelpers.CreateArmClientProvider(existingResourceIds: []));
        builder.Services.AddSingleton<ITokenCredentialProvider>(ProvisioningTestHelpers.CreateTokenCredentialProvider());
        builder.AddAzureProvisioning();

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var environmentResource = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());
        var controller = app.Services.GetRequiredService<AzureProvisioningController>();

        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        azureSection.Data["SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        azureSection.Data["Location"] = "westus2";
        azureSection.Data["ResourceGroup"] = "test-rg";
        await deploymentStateManager.SaveSectionAsync(azureSection);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Outputs"] = """{"id":{"value":"/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/storage"}}""";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        await notifications.PublishUpdateAsync(environmentResource, state => state with { State = KnownResourceStates.Running });
        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = KnownResourceStates.Running });

        await controller.CheckForDriftAsync(model);

        Assert.True(notifications.TryGetCurrentState(environmentResource.Name, out var environmentEvent));
        Assert.Equal(AzureProvisioningController.DriftedState, environmentEvent.Snapshot.State?.Text);
        Assert.Equal(KnownResourceStateStyles.Error, environmentEvent.Snapshot.State?.Style);

        Assert.True(notifications.TryGetCurrentState(storage.Resource.Name, out var resourceEvent));
        Assert.Equal(AzureProvisioningController.MissingInAzureState, resourceEvent.Snapshot.State?.Text);
        Assert.Equal(KnownResourceStateStyles.Error, resourceEvent.Snapshot.State?.Style);
    }

    [Fact]
    public async Task DeleteAzureResourcesCommand_DeletesCurrentResourceGroupAndPreservesAzureContextState()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var resourceGroup = new TestResourceGroupResource("test-rg");
        var testProvisioningContextProvider = new TestProvisioningContextProvider();

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<IArmClientProvider>();
        builder.Services.RemoveAll<ITokenCredentialProvider>();
        builder.Services.AddSingleton<IArmClientProvider>(new TestArmClientProvider(resourceGroup));
        builder.Services.AddSingleton<ITokenCredentialProvider>(ProvisioningTestHelpers.CreateTokenCredentialProvider());
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var environmentResource = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());

        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        azureSection.Data["SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        azureSection.Data["Location"] = "westus2";
        azureSection.Data["ResourceGroup"] = "test-rg";
        azureSection.Data["TenantId"] = "87654321-4321-4321-4321-210987654321";
        await deploymentStateManager.SaveSectionAsync(azureSection);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Id"] = "storage-deployment";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        var deleteCommand = Assert.Single(environmentResource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.DeleteAzureResourcesCommandName);

        var result = await deleteCommand.ExecuteCommand(new ExecuteCommandContext
        {
            ServiceProvider = app.Services,
            ResourceName = environmentResource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.True(result.Success);
        Assert.Equal("Azure resources deleted and provisioning state reset.", result.Message);
        Assert.Equal(1, resourceGroup.DeleteCallCount);
        Assert.Equal(0, testProvisioningContextProvider.CreateProvisioningContextCallCount);

        azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        Assert.Equal("12345678-1234-1234-1234-123456789012", azureSection.Data["SubscriptionId"]?.GetValue<string>());
        Assert.Equal("westus2", azureSection.Data["Location"]?.GetValue<string>());
        Assert.Equal("test-rg", azureSection.Data["ResourceGroup"]?.GetValue<string>());
        Assert.Equal("87654321-4321-4321-4321-210987654321", azureSection.Data["TenantId"]?.GetValue<string>());

        storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.Empty(storageSection.Data);

        Assert.Empty(storage.Resource.Outputs);
    }

    [Fact]
    public async Task DeleteAzureResourcesCommand_DoesNotDeleteConfiguredResourceGroupWhenPersistedContextIsMissing()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var resourceGroup = new TestResourceGroupResource("configured-rg");
        var testProvisioningContextProvider = new TestProvisioningContextProvider();

        builder.Configuration["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        builder.Configuration["Azure:Location"] = "westus2";
        builder.Configuration["Azure:ResourceGroup"] = "configured-rg";
        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<IArmClientProvider>();
        builder.Services.RemoveAll<ITokenCredentialProvider>();
        builder.Services.AddSingleton<IArmClientProvider>(new TestArmClientProvider(resourceGroup));
        builder.Services.AddSingleton<ITokenCredentialProvider>(ProvisioningTestHelpers.CreateTokenCredentialProvider());
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var environmentResource = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Id"] = "storage-deployment";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        var deleteCommand = Assert.Single(environmentResource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.DeleteAzureResourcesCommandName);

        var result = await deleteCommand.ExecuteCommand(new ExecuteCommandContext
        {
            ServiceProvider = app.Services,
            ResourceName = environmentResource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.True(result.Success);
        Assert.Equal("Azure resources deleted and provisioning state reset.", result.Message);
        Assert.Equal(0, resourceGroup.DeleteCallCount);
        Assert.Equal(0, testProvisioningContextProvider.CreateProvisioningContextCallCount);

        storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.Empty(storageSection.Data);
        Assert.Empty(storage.Resource.Outputs);
    }

    [Fact]
    public async Task DeleteAzureResourcesCommand_TreatsMissingResourceGroupAsSuccessAndClearsState()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<IArmClientProvider>();
        builder.Services.RemoveAll<ITokenCredentialProvider>();
        builder.Services.AddSingleton(ProvisioningTestHelpers.CreateArmClientProviderForMissingResourceGroup());
        builder.Services.AddSingleton<ITokenCredentialProvider>(ProvisioningTestHelpers.CreateTokenCredentialProvider());
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var environmentResource = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());

        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        azureSection.Data["SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        azureSection.Data["Location"] = "westus2";
        azureSection.Data["ResourceGroup"] = "missing-rg";
        await deploymentStateManager.SaveSectionAsync(azureSection);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Id"] = "storage-deployment";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        var deleteCommand = Assert.Single(environmentResource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.DeleteAzureResourcesCommandName);

        var result = await deleteCommand.ExecuteCommand(new ExecuteCommandContext
        {
            ServiceProvider = app.Services,
            ResourceName = environmentResource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.True(result.Success);
        Assert.Equal("Azure resources deleted and provisioning state reset.", result.Message);
        Assert.Equal(0, testProvisioningContextProvider.CreateProvisioningContextCallCount);

        storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.Empty(storageSection.Data);
        Assert.Empty(storage.Resource.Outputs);
    }

    [Fact]
    public async Task DeleteAzureResourcesCommand_PublishesFailureWhenResourceGroupDeleteFails()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();
        var resourceGroup = new TestResourceGroupResource("test-rg", new RequestFailedException(409, "Resource group is locked."));

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<IArmClientProvider>();
        builder.Services.RemoveAll<ITokenCredentialProvider>();
        builder.Services.AddSingleton<IArmClientProvider>(new TestArmClientProvider(resourceGroup));
        builder.Services.AddSingleton<ITokenCredentialProvider>(ProvisioningTestHelpers.CreateTokenCredentialProvider());
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var environmentResource = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();

        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        azureSection.Data["SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        azureSection.Data["Location"] = "westus2";
        azureSection.Data["ResourceGroup"] = "test-rg";
        await deploymentStateManager.SaveSectionAsync(azureSection);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Id"] = "storage-deployment";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        var deleteCommand = Assert.Single(environmentResource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.DeleteAzureResourcesCommandName);

        var result = await deleteCommand.ExecuteCommand(new ExecuteCommandContext
        {
            ServiceProvider = app.Services,
            ResourceName = environmentResource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.False(result.Success);
        Assert.Equal(1, resourceGroup.DeleteCallCount);
        Assert.Equal(0, testProvisioningContextProvider.CreateProvisioningContextCallCount);

        Assert.True(notifications.TryGetCurrentState(environmentResource.Name, out var environmentEvent));
        Assert.Equal("Failed to Delete", environmentEvent.Snapshot.State?.Text);

        storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.Equal("storage-deployment", storageSection.Data["Id"]?.GetValue<string>());
    }

    [Fact]
    public async Task EnsureProvisioned_WaitsForReferencedAzureResources()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();
        var testBicepProvisioner = new BlockingTestBicepProvisioner();

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

        var storage = new AzureProvisioningResource("storage", _ => { });
        storage.Outputs["name"] = "storage";
        var storageRoles = new AzureProvisioningResource("storage-roles", infra =>
        {
            new BicepOutputReference("name", storage).AsProvisioningParameter(infra);
        });
        builder.AddResource(storageRoles);
        builder.AddResource(storage);

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var controller = app.Services.GetRequiredService<AzureProvisioningController>();

        var reprovisionTask = controller.EnsureProvisionedAsync(model, CancellationToken.None);

        await testBicepProvisioner.FirstProvisionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["storage"], testBicepProvisioner.ProvisionedResources);

        testBicepProvisioner.AllowFirstProvisionToComplete.TrySetResult();
        await reprovisionTask;

        Assert.Equal(["storage", "storage-roles"], testBicepProvisioner.ProvisionedResources);
    }

    [Fact]
    public async Task EnsureProvisioned_FaultsDependentsWhenDependencyProvisioningFails()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            new ThrowingTestBicepProvisioner(),
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

        var storage = new AzureProvisioningResource("storage", _ => { });
        storage.Outputs["name"] = "storage";
        var storageRoles = new AzureProvisioningResource("storage-roles", infra =>
        {
            new BicepOutputReference("name", storage).AsProvisioningParameter(infra);
        });
        builder.AddResource(storageRoles);
        builder.AddResource(storage);

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var controller = app.Services.GetRequiredService<AzureProvisioningController>();

        await controller.EnsureProvisionedAsync(model).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(notifications.TryGetCurrentState(storage.Name, out var storageEvent));
        Assert.Equal("Failed to Provision", storageEvent.Snapshot.State?.Text);

        Assert.True(notifications.TryGetCurrentState(storageRoles.Name, out var storageRolesEvent));
        Assert.Equal("Failed to Provision", storageRolesEvent.Snapshot.State?.Text);
    }

    [Fact]
    public void AddAzureEnvironment_InPublishMode_CreatesStableDeploymentName()
    {
        // Arrange
        var builder = CreateBuilder(isRunMode: false);
        builder.Configuration["AppHost:ProjectNameSha256"] = "ABCDE12345";

        // Act
        builder.AddAzureEnvironment();

        // Assert
        var resource = builder.Resources.OfType<AzureEnvironmentResource>().Single();
        Assert.Equal("azure-environment", resource.Name);
    }

    [Fact]
    public void AddAzureEnvironment_InRunMode_CreatesDiscoverableControlResourceName()
    {
        // Arrange
        var builder = CreateBuilder(isRunMode: true);

        // Act
        builder.AddAzureEnvironment();

        // Assert
        var resource = builder.Resources.OfType<AzureEnvironmentResource>().Single();
        Assert.Equal("azure-environment", resource.Name);
    }

    [Fact]
    public void AzureEnvironmentResource_PreservesDefaultResourceNameValidation()
    {
        var builder = CreateBuilder(isRunMode: true);
        var location = new ParameterResource("location", _ => "westus");
        var resourceGroupName = new ParameterResource("resourceGroupName", _ => "rg");
        var principalId = new ParameterResource("principalId", _ => "principal");
        var resource = new AzureEnvironmentResource("azure_environment", location, resourceGroupName, principalId);

        var ex = Assert.Throws<ArgumentException>(() => builder.AddResource(resource));
        Assert.Equal("Resource name 'azure_environment' is invalid. Name must contain only ASCII letters, digits, and hyphens. (Parameter 'name')", ex.Message);
    }

    [Fact]
    public void AddAzureEnvironment_CreatesFallbackNameWhenAzureResourceNameExists()
    {
        // Arrange
        var builder = CreateBuilder(isRunMode: true);
        builder.AddParameter("azure-environment", "value");

        // Act
        builder.AddAzureEnvironment();

        // Assert
        var resource = builder.Resources.OfType<AzureEnvironmentResource>().Single();
        Assert.Equal("azure-environment2", resource.Name);
    }

    [Fact]
    public void WithLocation_ShouldSetLocationProperty()
    {
        // Arrange
        var builder = CreateBuilder(isRunMode: false);
        var resourceBuilder = builder.AddAzureEnvironment();
        var expectedLocation = builder.AddParameter("location", "eastus2");

        // Act
        resourceBuilder.WithLocation(expectedLocation);

        // Assert
        var resource = builder.Resources.OfType<AzureEnvironmentResource>().Single();
        Assert.Equal(expectedLocation.Resource, resource.Location);
    }

    [Fact]
    public void WithResourceGroup_ShouldSetResourceGroupNameProperty()
    {
        // Arrange
        var builder = CreateBuilder(isRunMode: false);
        var resourceBuilder = builder.AddAzureEnvironment();
        var expectedResourceGroup = builder.AddParameter("resourceGroupName", "my-resource-group");

        // Act
        resourceBuilder.WithResourceGroup(expectedResourceGroup);

        // Assert
        var resource = builder.Resources.OfType<AzureEnvironmentResource>().Single();
        Assert.Equal(expectedResourceGroup.Resource, resource.ResourceGroupName);
    }

    private static IDistributedApplicationBuilder CreateBuilder(bool isRunMode = false)
    {
        var operation = isRunMode ? DistributedApplicationOperation.Run : DistributedApplicationOperation.Publish;
        return TestDistributedApplicationBuilder.Create(operation);
    }

    private static InteractionInputCollection CreateArguments(params (string Name, string? Value)[] values)
    {
        return new InteractionInputCollection([.. values.Select(static value => new InteractionInput
        {
            Name = value.Name,
            InputType = InputType.Text,
            Value = value.Value
        })]);
    }

    private static InteractionInputCollection CloneInputs(IReadOnlyList<InteractionInput> inputs)
    {
        return new InteractionInputCollection([.. inputs.Select(static input => new InteractionInput
        {
            Name = input.Name,
            Label = input.Label,
            Description = input.Description,
            EnableDescriptionMarkdown = input.EnableDescriptionMarkdown,
            InputType = input.InputType,
            Required = input.Required,
            Options = input.Options,
            DynamicLoading = input.DynamicLoading,
            Value = input.Value,
            Placeholder = input.Placeholder,
            AllowCustomChoice = input.AllowCustomChoice,
            Disabled = input.Disabled,
            MaxLength = input.MaxLength
        })]);
    }

    private static JsonObject AssertCommandJsonData(ExecuteCommandResult result)
    {
        Assert.NotNull(result.Data);
        var data = result.Data!;
        Assert.Equal(CommandResultFormat.Json, data.Format);
        return Assert.IsType<JsonObject>(JsonNode.Parse(data.Value));
    }

    private static async Task<(IReadOnlyList<PipelineStep> Steps, PipelineContext PipelineContext)> CreateAzureEnvironmentPipelineStepsAsync(
        AzureEnvironmentResource environmentResource,
        DistributedApplicationModel model,
        IServiceProvider services)
    {
        var pipelineContext = new PipelineContext(
            model,
            services.GetRequiredService<DistributedApplicationExecutionContext>(),
            services,
            NullLogger.Instance,
            CancellationToken.None);

        var annotation = Assert.Single(environmentResource.Annotations.OfType<PipelineStepAnnotation>());
        var steps = await annotation.CreateStepsAsync(new PipelineStepFactoryContext
        {
            PipelineContext = pipelineContext,
            Resource = environmentResource
        });

        return ([.. steps], pipelineContext);
    }

    private sealed class AnnotatedAzureResource(string name) : Resource(name);

    private sealed class TestDeploymentStateManager : IDeploymentStateManager
    {
        private readonly Dictionary<string, JsonObject> _sections = new(StringComparer.Ordinal);

        public string? StateFilePath => null;

        public Task<DeploymentStateSection> AcquireSectionAsync(string sectionName, CancellationToken cancellationToken = default)
        {
            _sections.TryGetValue(sectionName, out var existingData);
            var data = existingData?.DeepClone().AsObject() ?? [];

            return Task.FromResult(new DeploymentStateSection(sectionName, data, version: 0));
        }

        public Task DeleteSectionAsync(DeploymentStateSection section, CancellationToken cancellationToken = default)
        {
            _sections.Remove(section.SectionName);
            return Task.CompletedTask;
        }

        public Task SaveSectionAsync(DeploymentStateSection section, CancellationToken cancellationToken = default)
        {
            _sections[section.SectionName] = section.Data.DeepClone().AsObject();
            return Task.CompletedTask;
        }

        public Task ClearAllStateAsync(CancellationToken cancellationToken = default)
        {
            _sections.Clear();
            return Task.CompletedTask;
        }
    }

    private sealed class TestBicepProvisioner : IBicepProvisioner
    {
        public int ConfigureResourceCallCount { get; private set; }

        public int GetOrCreateResourceCallCount { get; private set; }

        public List<string> ConfiguredResources { get; } = [];

        public List<string> ProvisionedResources { get; } = [];
        public Dictionary<string, string?> ProvisionedLocations { get; } = new(StringComparer.Ordinal);

        public Task<bool> ConfigureResourceAsync(AzureBicepResource resource, CancellationToken cancellationToken)
        {
            ConfigureResourceCallCount++;
            ConfiguredResources.Add(resource.Name);
            return Task.FromResult(false);
        }

        public Task GetOrCreateResourceAsync(AzureBicepResource resource, ProvisioningContext context, CancellationToken cancellationToken)
        {
            GetOrCreateResourceCallCount++;
            ProvisionedResources.Add(resource.Name);
            ProvisionedLocations[resource.Name] = resource.Parameters.TryGetValue(AzureBicepResource.KnownParameters.Location, out var location)
                ? location?.ToString()
                : null;
            resource.Outputs["blobEndpoint"] = "https://storage.blob.core.windows.net/";
            return Task.CompletedTask;
        }
    }

    private sealed class CachedStateTestBicepProvisioner : IBicepProvisioner
    {
        public int ConfigureResourceCallCount { get; private set; }

        public int GetOrCreateResourceCallCount { get; private set; }

        public Task<bool> ConfigureResourceAsync(AzureBicepResource resource, CancellationToken cancellationToken)
        {
            ConfigureResourceCallCount++;
            resource.Outputs["blobEndpoint"] = "https://storage.blob.core.windows.net/";
            return Task.FromResult(true);
        }

        public Task GetOrCreateResourceAsync(AzureBicepResource resource, ProvisioningContext context, CancellationToken cancellationToken)
        {
            GetOrCreateResourceCallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class TestProvisioningContextProvider : IProvisioningContextProvider
    {
        private readonly ProvisioningContext _context;

        public TestProvisioningContextProvider()
            : this(ProvisioningTestHelpers.CreateTestProvisioningContext())
        {
        }

        public TestProvisioningContextProvider(ProvisioningContext context)
        {
            _context = context;
        }

        public int CreateProvisioningContextCallCount { get; private set; }

        public Task<ProvisioningContext> CreateProvisioningContextAsync(CancellationToken cancellationToken = default)
        {
            CreateProvisioningContextCallCount++;
            return Task.FromResult(_context);
        }
    }

    private sealed class BlockingTestBicepProvisioner : IBicepProvisioner
    {
        public TaskCompletionSource FirstProvisionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowFirstProvisionToComplete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> ProvisionedResources { get; } = [];

        public Task<bool> ConfigureResourceAsync(AzureBicepResource resource, CancellationToken cancellationToken) => Task.FromResult(false);

        public async Task GetOrCreateResourceAsync(AzureBicepResource resource, ProvisioningContext context, CancellationToken cancellationToken)
        {
            ProvisionedResources.Add(resource.Name);
            if (ProvisionedResources.Count == 1)
            {
                FirstProvisionStarted.TrySetResult();
                await AllowFirstProvisionToComplete.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            resource.Outputs["blobEndpoint"] = $"https://{resource.Name}.blob.core.windows.net/";
        }
    }

    private sealed class ThrowingTestBicepProvisioner : IBicepProvisioner
    {
        public Task<bool> ConfigureResourceAsync(AzureBicepResource resource, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task GetOrCreateResourceAsync(AzureBicepResource resource, ProvisioningContext context, CancellationToken cancellationToken)
            => Task.FromException(new InvalidOperationException("boom"));
    }

    private sealed class CancelConflictArmClientProvider : IArmClientProvider
    {
        public IArmClient GetArmClient(global::Azure.Core.TokenCredential credential, string subscriptionId)
            => new CancelConflictArmClient();

        public IArmClient GetArmClient(global::Azure.Core.TokenCredential credential)
            => new CancelConflictArmClient();
    }

    private sealed class CancelConflictArmClient : IArmClient
    {
        public Task<(ISubscriptionResource subscription, ITenantResource tenant)> GetSubscriptionAndTenantAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IEnumerable<ITenantResource>> GetAvailableTenantsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IEnumerable<ISubscriptionResource>> GetAvailableSubscriptionsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IEnumerable<ISubscriptionResource>> GetAvailableSubscriptionsAsync(string? tenantId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IEnumerable<(string Name, string DisplayName)>> GetAvailableLocationsAsync(string subscriptionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IEnumerable<(string Name, string Location)>> GetAvailableResourceGroupsWithLocationAsync(string subscriptionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IRoleAssignmentCollection GetRoleAssignments(ResourceIdentifier scope)
            => throw new NotSupportedException();

        public Task<bool> ResourceExistsAsync(string resourceId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteResourceAsync(string resourceId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CancelDeploymentAsync(string deploymentId, CancellationToken cancellationToken = default)
            => throw new RequestFailedException(409, "The deployment is already completed.");

        public async IAsyncEnumerable<string> GetDeploymentTargetResourceIdsAsync(string deploymentId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class CredentialUnavailableArmClientProvider : IArmClientProvider
    {
        public IArmClient GetArmClient(global::Azure.Core.TokenCredential credential, string subscriptionId)
            => new CredentialUnavailableArmClient();

        public IArmClient GetArmClient(global::Azure.Core.TokenCredential credential)
            => new CredentialUnavailableArmClient();
    }

    private sealed class CredentialUnavailableArmClient : IArmClient
    {
        public Task<(ISubscriptionResource subscription, ITenantResource tenant)> GetSubscriptionAndTenantAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IEnumerable<ITenantResource>> GetAvailableTenantsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IEnumerable<ISubscriptionResource>> GetAvailableSubscriptionsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IEnumerable<ISubscriptionResource>> GetAvailableSubscriptionsAsync(string? tenantId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IEnumerable<(string Name, string DisplayName)>> GetAvailableLocationsAsync(string subscriptionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IEnumerable<(string Name, string Location)>> GetAvailableResourceGroupsWithLocationAsync(string subscriptionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IRoleAssignmentCollection GetRoleAssignments(global::Azure.Core.ResourceIdentifier scope)
            => throw new NotSupportedException();

        public Task<bool> ResourceExistsAsync(string resourceId, CancellationToken cancellationToken = default)
            => Task.FromException<bool>(new global::Azure.Identity.CredentialUnavailableException("Credential unavailable."));

        public Task DeleteResourceAsync(string resourceId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CancelDeploymentAsync(string deploymentId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<string> GetDeploymentTargetResourceIdsAsync(string deploymentId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class ThrowingResourceProbeArmClientProvider(RequestFailedException exception) : IArmClientProvider
    {
        public IArmClient GetArmClient(TokenCredential credential, string subscriptionId)
            => new ThrowingResourceProbeArmClient(exception);

        public IArmClient GetArmClient(TokenCredential credential)
            => new ThrowingResourceProbeArmClient(exception);
    }

    private sealed class ThrowingResourceProbeArmClient(RequestFailedException exception) : IArmClient
    {
        private readonly TestArmClient _inner = new();

        public Task<(ISubscriptionResource subscription, ITenantResource tenant)> GetSubscriptionAndTenantAsync(CancellationToken cancellationToken = default)
            => _inner.GetSubscriptionAndTenantAsync(cancellationToken);

        public Task<IEnumerable<ITenantResource>> GetAvailableTenantsAsync(CancellationToken cancellationToken = default)
            => _inner.GetAvailableTenantsAsync(cancellationToken);

        public Task<IEnumerable<ISubscriptionResource>> GetAvailableSubscriptionsAsync(CancellationToken cancellationToken = default)
            => _inner.GetAvailableSubscriptionsAsync(cancellationToken);

        public Task<IEnumerable<ISubscriptionResource>> GetAvailableSubscriptionsAsync(string? tenantId, CancellationToken cancellationToken = default)
            => _inner.GetAvailableSubscriptionsAsync(tenantId, cancellationToken);

        public Task<IEnumerable<(string Name, string DisplayName)>> GetAvailableLocationsAsync(string subscriptionId, CancellationToken cancellationToken = default)
            => _inner.GetAvailableLocationsAsync(subscriptionId, cancellationToken);

        public Task<IEnumerable<(string Name, string Location)>> GetAvailableResourceGroupsWithLocationAsync(string subscriptionId, CancellationToken cancellationToken = default)
            => _inner.GetAvailableResourceGroupsWithLocationAsync(subscriptionId, cancellationToken);

        public IRoleAssignmentCollection GetRoleAssignments(ResourceIdentifier scope)
            => _inner.GetRoleAssignments(scope);

        public Task<bool> ResourceExistsAsync(string resourceId, CancellationToken cancellationToken = default)
            => Task.FromException<bool>(exception);

        public Task DeleteResourceAsync(string resourceId, CancellationToken cancellationToken = default)
            => _inner.DeleteResourceAsync(resourceId, cancellationToken);

        public Task CancelDeploymentAsync(string deploymentId, CancellationToken cancellationToken = default)
            => _inner.CancelDeploymentAsync(deploymentId, cancellationToken);

        public IAsyncEnumerable<string> GetDeploymentTargetResourceIdsAsync(string deploymentId, CancellationToken cancellationToken = default)
            => _inner.GetDeploymentTargetResourceIdsAsync(deploymentId, cancellationToken);
    }

    private sealed class DeleteResourceFailureArmClientProvider(string existingResourceId, RequestFailedException deleteException) : IArmClientProvider
    {
        public IArmClient GetArmClient(TokenCredential credential, string subscriptionId)
            => new DeleteResourceFailureArmClient(existingResourceId, deleteException);

        public IArmClient GetArmClient(TokenCredential credential)
            => new DeleteResourceFailureArmClient(existingResourceId, deleteException);
    }

    private sealed class DeleteResourceFailureArmClient(string existingResourceId, RequestFailedException deleteException) : IArmClient
    {
        private readonly TestArmClient _inner = new();

        public Task<(ISubscriptionResource subscription, ITenantResource tenant)> GetSubscriptionAndTenantAsync(CancellationToken cancellationToken = default)
            => _inner.GetSubscriptionAndTenantAsync(cancellationToken);

        public Task<IEnumerable<ITenantResource>> GetAvailableTenantsAsync(CancellationToken cancellationToken = default)
            => _inner.GetAvailableTenantsAsync(cancellationToken);

        public Task<IEnumerable<ISubscriptionResource>> GetAvailableSubscriptionsAsync(CancellationToken cancellationToken = default)
            => _inner.GetAvailableSubscriptionsAsync(cancellationToken);

        public Task<IEnumerable<ISubscriptionResource>> GetAvailableSubscriptionsAsync(string? tenantId, CancellationToken cancellationToken = default)
            => _inner.GetAvailableSubscriptionsAsync(tenantId, cancellationToken);

        public Task<IEnumerable<(string Name, string DisplayName)>> GetAvailableLocationsAsync(string subscriptionId, CancellationToken cancellationToken = default)
            => _inner.GetAvailableLocationsAsync(subscriptionId, cancellationToken);

        public Task<IEnumerable<(string Name, string Location)>> GetAvailableResourceGroupsWithLocationAsync(string subscriptionId, CancellationToken cancellationToken = default)
            => _inner.GetAvailableResourceGroupsWithLocationAsync(subscriptionId, cancellationToken);

        public IRoleAssignmentCollection GetRoleAssignments(ResourceIdentifier scope)
            => _inner.GetRoleAssignments(scope);

        public Task<bool> ResourceExistsAsync(string resourceId, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Equals(resourceId, existingResourceId, StringComparison.OrdinalIgnoreCase));

        public Task DeleteResourceAsync(string resourceId, CancellationToken cancellationToken = default)
            => string.Equals(resourceId, existingResourceId, StringComparison.OrdinalIgnoreCase)
                ? Task.FromException(deleteException)
                : _inner.DeleteResourceAsync(resourceId, cancellationToken);

        public Task CancelDeploymentAsync(string deploymentId, CancellationToken cancellationToken = default)
            => _inner.CancelDeploymentAsync(deploymentId, cancellationToken);

        public IAsyncEnumerable<string> GetDeploymentTargetResourceIdsAsync(string deploymentId, CancellationToken cancellationToken = default)
            => _inner.GetDeploymentTargetResourceIdsAsync(deploymentId, cancellationToken);
    }
}
