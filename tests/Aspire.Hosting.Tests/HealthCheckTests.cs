// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using System.Text;
using Aspire.TestUtilities;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Tests.Helpers;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "3")]
public class HealthCheckTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public void WithHttpHealthCheckThrowsIfReferencingEndpointByNameThatIsNotHttpScheme()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var container = builder.AddContainer("resource", "dummycontainer")
                .WithEndpoint(targetPort: 9999, scheme: "tcp", name: "nonhttp");

        var ex = Assert.Throws<DistributedApplicationException>(() =>
        {
            container.WithHttpHealthCheck(endpointName: "nonhttp");
        });

        Assert.Equal(
            "Could not create HTTP health check for resource 'resource' as the endpoint with name 'nonhttp' and scheme 'tcp' is not an HTTP endpoint.",
            ex.Message
            );
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public void WithHttpHealthCheckThrowsIfReferencingEndpointThatIsNotHttpScheme()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var container = builder.AddContainer("resource", "dummycontainer")
                .WithEndpoint(targetPort: 9999, scheme: "tcp", name: "nonhttp");

        var ex = Assert.Throws<DistributedApplicationException>(() =>
        {
            container.WithHttpHealthCheck(() => container.GetEndpoint("nonhttp"));
        });

        Assert.Equal(
            "Could not create HTTP health check for resource 'resource' as the endpoint with name 'nonhttp' and scheme 'tcp' is not an HTTP endpoint.",
            ex.Message
            );
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public void WithHttpsHealthCheckThrowsIfReferencingEndpointThatIsNotHttpsScheme()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var ex = Assert.Throws<DistributedApplicationException>(() =>
        {
#pragma warning disable CS0618 // Type or member is obsolete
            builder.AddContainer("resource", "dummycontainer")
                .WithEndpoint(targetPort: 9999, scheme: "tcp", name: "nonhttp")
                .WithHttpsHealthCheck(endpointName: "nonhttp");
#pragma warning restore CS0618 // Type or member is obsolete
        });

        Assert.Equal(
            "Could not create HTTP health check for resource 'resource' as the endpoint with name 'nonhttp' and scheme 'tcp' is not an HTTP endpoint.",
            ex.Message
            );
    }

    [Fact]
    public async Task WithHttpHealthCheckInitializesUriOnBeforeResourceStartedEvent()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var stalePort = await Network.GetAvailablePortAsync();
        var healthyPort = await Network.GetAvailablePortAsync();
        while (healthyPort == stalePort)
        {
            healthyPort = await Network.GetAvailablePortAsync();
        }

        using var serverCts = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Loopback, healthyPort);
        listener.Start();
        var serverTask = RunHealthyHttpServerAsync(listener, serverCts.Token);

        try
        {
            var resource = builder.AddContainer("resource", "dummycontainer")
                .WithHttpEndpoint(port: stalePort, targetPort: 80)
                .WithHttpHealthCheck();

            using var app = builder.Build();

            var endpoint = resource.GetEndpoint("http").EndpointAnnotation;
            endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, IPAddress.Loopback.ToString(), stalePort, EndpointBindingMode.SingleAddress, targetPortExpression: null, networkId: null);

            var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();
            var registration = app.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
                .Single(r => r.Name == "resource_http_/_200_check");

            await eventing.PublishAsync(new ResourceEndpointsAllocatedEvent(resource.Resource, app.Services));

            // Change the endpoint after allocation so this test proves the health check URI is initialized
            // from BeforeResourceStartedEvent rather than ResourceEndpointsAllocatedEvent.
            endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, IPAddress.Loopback.ToString(), healthyPort, EndpointBindingMode.SingleAddress, targetPortExpression: null, networkId: null);

            await eventing.PublishAsync(new BeforeResourceStartedEvent(resource.Resource, app.Services));

            var healthCheck = registration.Factory(app.Services);
            var result = await healthCheck.CheckHealthAsync(
                new HealthCheckContext { Registration = registration },
                TestContext.Current.CancellationToken);
            Assert.Equal(HealthStatus.Healthy, result.Status);
        }
        finally
        {
            await serverCts.CancelAsync();
            listener.Stop();
            await serverTask.DefaultTimeout();
        }
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyWithHttpHealthCheckBlocksDependentResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(testOutputHelper);

        var healthCheckTcs = new TaskCompletionSource<HealthCheckResult>();
        builder.Services.AddHealthChecks().AddAsyncCheck("blocking_check", () =>
        {
            return healthCheckTcs.Task;
        });

        var resource = builder.AddContainer("resource", "mcr.microsoft.com/cbl-mariner/base/nginx", "1.22")
                              .WithHttpEndpoint(targetPort: 80)
                              .WithHttpHealthCheck(statusCode: 404)
                              .WithHealthCheck("blocking_check");

        var dependentResource = builder.AddContainer("dependentresource", "mcr.microsoft.com/cbl-mariner/base/nginx", "1.22")
                                       .WaitFor(resource);

        using var app = builder.Build();

        var pendingStart = app.StartAsync();

        var rns = app.Services.GetRequiredService<ResourceNotificationService>();

        await rns.WaitForResourceAsync(resource.Resource.Name, KnownResourceStates.Running).DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        await rns.WaitForResourceAsync(dependentResource.Resource.Name, KnownResourceStates.Waiting).DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        healthCheckTcs.SetResult(HealthCheckResult.Healthy());

        await rns.WaitForResourceHealthyAsync(resource.Resource.Name).DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        await rns.WaitForResourceAsync(dependentResource.Resource.Name, KnownResourceStates.Running).DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        await pendingStart.DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);

        await app.StopAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);
    }

    [Fact]
    public async Task BuildThrowsOnMissingHealthCheckRegistration()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        builder.Services.AddLogging(b => {
            b.AddFakeLogging();
        });

        builder.AddResource(new CustomResource("test"))
               .WithHealthCheck("test_check");
        var app = builder.Build();

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(async () =>
        {
            await app.StartAsync();
        }).DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);

        Assert.Equal("A health check registration is missing. Check logs for more details.", ex.Message);

        var collector = app.Services.GetFakeLogCollector();
        var logs = collector.GetSnapshot();

        Assert.Contains(
            logs,
            l => l.Message == "The health check 'test_check' is not registered and is required for resource 'test'."
            );
    }

    private static async Task RunHealthyHttpServerAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                await using var stream = client.GetStream();
                await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private sealed class CustomChildResource(string name, CustomResource parent) : Resource(name), IResourceWithParent<CustomResource>
    {
        public CustomResource Parent => parent;
    }

    private sealed class CustomResource(string name) : Resource(name)
    {
    }
}
