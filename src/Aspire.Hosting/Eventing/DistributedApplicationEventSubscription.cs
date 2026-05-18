// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Diagnostics;

namespace Aspire.Hosting.Eventing;

/// <summary>
/// Represents a subscription to an event that is published during the lifecycle of the AppHost.
/// </summary>
[AspireExport]
public class DistributedApplicationEventSubscription
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DistributedApplicationEventSubscription"/> class.
    /// </summary>
    /// <param name="callback">Callback to invoke when the event is published.</param>
    public DistributedApplicationEventSubscription(Func<IDistributedApplicationEvent, CancellationToken, Task> callback)
        : this(callback, callback)
    {
    }

    internal DistributedApplicationEventSubscription(Func<IDistributedApplicationEvent, CancellationToken, Task> callback, Delegate callbackForProfiling)
    {
        Callback = callback;
        CallbackDisplayName = ProfilingTelemetry.GetCallbackDisplayName(callbackForProfiling);
    }

    /// <summary>
    /// The callback to be executed when the event is published.
    /// </summary>
    public Func<IDistributedApplicationEvent, CancellationToken, Task> Callback { get; }

    internal string CallbackDisplayName { get; }
}

/// <summary>
/// Represents a subscription to an event that is published during the lifecycle of the AppHost for a specific resource.
/// </summary>
[AspireExport]
public class DistributedApplicationResourceEventSubscription : DistributedApplicationEventSubscription
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DistributedApplicationResourceEventSubscription"/> class.
    /// </summary>
    /// <param name="resource">Resource associated with this subscription.</param>
    /// <param name="callback">Callback to invoke when the event is published.</param>
    public DistributedApplicationResourceEventSubscription(IResource? resource, Func<IDistributedApplicationResourceEvent, CancellationToken, Task> callback)
        : base((@event, cancellationToken) => callback((IDistributedApplicationResourceEvent)@event, cancellationToken), callback)
    {
        Resource = resource;
    }

    /// <summary>
    /// Resource associated with this subscription.
    /// </summary>
    public IResource? Resource { get; }
}
