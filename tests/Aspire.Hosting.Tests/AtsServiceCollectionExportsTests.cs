// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Linq.Expressions;
using System.Reflection;
using Aspire.Hosting.Ats;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using AtsHealthCheckResult = Aspire.Hosting.Ats.HealthCheckResult;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "5")]
public class AtsServiceCollectionExportsTests
{
    [Fact]
    public void TryAddEventingSubscriber_AllowsDistinctCallbacks()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var method = typeof(DistributedApplication).Assembly
            .GetType("Aspire.Hosting.Ats.ServiceCollectionExports", throwOnError: true)!
            .GetMethod("TryAddEventingSubscriber", BindingFlags.Public | BindingFlags.Static)!;
        var callbackSubscriberType = method.DeclaringType!.GetNestedType("CallbackEventingSubscriber", BindingFlags.NonPublic)!;

        var firstSubscriber = CreateCallback(method.GetParameters()[1].ParameterType);
        var secondSubscriber = CreateCallback(method.GetParameters()[1].ParameterType);

        method.Invoke(null, [builder, firstSubscriber]);
        method.Invoke(null, [builder, firstSubscriber]);
        method.Invoke(null, [builder, secondSubscriber]);

        var subscribers = builder.Services
            .Where(descriptor => descriptor.ServiceType == typeof(IDistributedApplicationEventingSubscriber) &&
                                 descriptor.ImplementationInstance?.GetType() == callbackSubscriberType)
            .Select(descriptor => descriptor.ImplementationInstance)
            .ToList();

        Assert.Equal(2, subscribers.Count);
    }

    [Fact]
    public async Task AddHealthCheck_RegistersCallback()
    {
        var builder = DistributedApplication.CreateBuilder([]);

        builder.AddHealthCheck("custom_check", () => Task.FromResult(new AtsHealthCheckResult
        {
            Status = HealthStatus.Degraded,
            Description = "custom description"
        }));

        using var serviceProvider = builder.Services.BuildServiceProvider();
        var healthCheckService = serviceProvider.GetRequiredService<HealthCheckService>();

        var report = await healthCheckService.CheckHealthAsync(registration => registration.Name == "custom_check");
        var entry = Assert.Single(report.Entries);

        Assert.Equal("custom_check", entry.Key);
        Assert.Equal(HealthStatus.Degraded, entry.Value.Status);
        Assert.Equal("custom description", entry.Value.Description);
    }

    private static Delegate CreateCallback(Type delegateType)
    {
        var contextType = delegateType.GenericTypeArguments[0];
        var contextParameter = Expression.Parameter(contextType, "context");
        var completedTask = Expression.Property(null, typeof(Task).GetProperty(nameof(Task.CompletedTask))!);

        return Expression.Lambda(delegateType, completedTask, contextParameter).Compile();
    }
}
