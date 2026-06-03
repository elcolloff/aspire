// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ExtensionsHealthCheckResult = Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult;

namespace Aspire.Hosting.Ats;

/// <summary>
/// ATS exports for distributed-application builder and service-provider helpers.
/// </summary>
internal static class ServiceCollectionExports
{
    /// <summary>
    /// Adds an ATS-friendly eventing subscriber callback to the distributed-application builder.
    /// </summary>
    /// <param name="builder">The distributed-application builder.</param>
    /// <param name="subscribe">The callback that registers the event subscriptions.</param>
    [AspireExport]
    public static void AddEventingSubscriber(this IDistributedApplicationBuilder builder, Func<EventingSubscriberRegistrationContext, Task> subscribe)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(subscribe);

        builder.Services.AddSingleton<IDistributedApplicationEventingSubscriber>(new CallbackEventingSubscriber(subscribe));
    }

    /// <summary>
    /// Attempts to add an ATS-friendly eventing subscriber callback to the distributed-application builder.
    /// </summary>
    /// <param name="builder">The distributed-application builder.</param>
    /// <param name="subscribe">The callback that registers the event subscriptions.</param>
    [AspireExport]
    public static void TryAddEventingSubscriber(this IDistributedApplicationBuilder builder, Func<EventingSubscriberRegistrationContext, Task> subscribe)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(subscribe);

        if (builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IDistributedApplicationEventingSubscriber) &&
                                               descriptor.ImplementationInstance is CallbackEventingSubscriber existing &&
                                               existing.Matches(subscribe)))
        {
            return;
        }

        builder.Services.AddSingleton<IDistributedApplicationEventingSubscriber>(new CallbackEventingSubscriber(subscribe));
    }

    /// <summary>
    /// Gets the Aspire store from the service provider.
    /// </summary>
    /// <param name="serviceProvider">The service provider.</param>
    /// <returns>The Aspire store.</returns>
    [AspireExport]
    public static IAspireStore GetAspireStore(this IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        return serviceProvider.GetRequiredService<IAspireStore>();
    }

    /// <summary>
    /// Adds a custom health check callback to the distributed-application builder.
    /// </summary>
    /// <param name="builder">The distributed-application builder.</param>
    /// <param name="name">The health check registration name.</param>
    /// <param name="check">The callback that evaluates the health check.</param>
    [AspireExport]
    public static void AddHealthCheck(this IDistributedApplicationBuilder builder, string name, Func<Task<HealthCheckResult>> check)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(check);

        builder.Services.AddHealthChecks().AddAsyncCheck(name, async () =>
        {
            var result = await check().ConfigureAwait(false);
            return result.ToExtensionsHealthCheckResult();
        });
    }

    /// <summary>
    /// Subscribes to the BeforeStart event from an eventing subscriber registration context.
    /// </summary>
    /// <param name="context">The eventing subscriber registration context.</param>
    /// <param name="callback">The callback to invoke when the event fires.</param>
    /// <returns>The event subscription.</returns>
    [AspireExport("eventingSubscriberOnBeforeStart", MethodName = "onBeforeStart")]
    public static DistributedApplicationEventSubscription OnBeforeStart(this EventingSubscriberRegistrationContext context, Func<BeforeStartEvent, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(callback);

        return context.Eventing.Subscribe<BeforeStartEvent>((@event, _) => callback(@event));
    }

    /// <summary>
    /// Subscribes to the BeforePublish event from an eventing subscriber registration context.
    /// </summary>
    /// <param name="context">The eventing subscriber registration context.</param>
    /// <param name="callback">The callback to invoke when the event fires.</param>
    /// <returns>The event subscription.</returns>
    [AspireExport("eventingSubscriberOnBeforePublish", MethodName = "onBeforePublish")]
    public static DistributedApplicationEventSubscription OnBeforePublish(this EventingSubscriberRegistrationContext context, Func<BeforePublishEvent, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(callback);

        return context.Eventing.Subscribe<BeforePublishEvent>((@event, _) => callback(@event));
    }

    /// <summary>
    /// Subscribes to the AfterPublish event from an eventing subscriber registration context.
    /// </summary>
    /// <param name="context">The eventing subscriber registration context.</param>
    /// <param name="callback">The callback to invoke when the event fires.</param>
    /// <returns>The event subscription.</returns>
    [AspireExport("eventingSubscriberOnAfterPublish", MethodName = "onAfterPublish")]
    public static DistributedApplicationEventSubscription OnAfterPublish(this EventingSubscriberRegistrationContext context, Func<AfterPublishEvent, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(callback);

        return context.Eventing.Subscribe<AfterPublishEvent>((@event, _) => callback(@event));
    }

    /// <summary>
    /// Subscribes to the AfterResourcesCreated event from an eventing subscriber registration context.
    /// </summary>
    /// <param name="context">The eventing subscriber registration context.</param>
    /// <param name="callback">The callback to invoke when the event fires.</param>
    /// <returns>The event subscription.</returns>
    [AspireExport("eventingSubscriberOnAfterResourcesCreated", MethodName = "onAfterResourcesCreated")]
    public static DistributedApplicationEventSubscription OnAfterResourcesCreated(this EventingSubscriberRegistrationContext context, Func<AfterResourcesCreatedEvent, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(callback);

        return context.Eventing.Subscribe<AfterResourcesCreatedEvent>((@event, _) => callback(@event));
    }

    private sealed class CallbackEventingSubscriber(Func<EventingSubscriberRegistrationContext, Task> subscribe) : IDistributedApplicationEventingSubscriber
    {
        public bool Matches(Func<EventingSubscriberRegistrationContext, Task> otherSubscribe)
        {
            return subscribe == otherSubscribe;
        }

        public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
        {
            return subscribe(new EventingSubscriberRegistrationContext(eventing, executionContext, cancellationToken));
        }
    }
}

/// <summary>
/// ATS-friendly custom health check result.
/// </summary>
[AspireDto]
internal sealed class HealthCheckResult
{
    /// <summary>
    /// Gets the health status returned by the health check.
    /// </summary>
    public required HealthStatus Status { get; init; }

    /// <summary>
    /// Gets an optional description for the health check result.
    /// </summary>
    public string? Description { get; init; }

    internal ExtensionsHealthCheckResult ToExtensionsHealthCheckResult()
    {
        return new ExtensionsHealthCheckResult(Status, Description);
    }
}

/// <summary>
/// Context passed to ATS-friendly eventing subscriber registrations.
/// </summary>
[AspireExport(ExposeProperties = true)]
internal sealed class EventingSubscriberRegistrationContext(
    IDistributedApplicationEventing eventing,
    DistributedApplicationExecutionContext executionContext,
    CancellationToken cancellationToken)
{
    internal IDistributedApplicationEventing Eventing { get; } = eventing;

    /// <summary>
    /// The execution context for the AppHost invocation.
    /// </summary>
    public DistributedApplicationExecutionContext ExecutionContext { get; } = executionContext;

    /// <summary>
    /// The cancellation token associated with the subscriber registration.
    /// </summary>
    public CancellationToken CancellationToken { get; } = cancellationToken;
}
