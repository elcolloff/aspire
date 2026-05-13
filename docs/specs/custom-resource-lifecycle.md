# Custom Resource Lifecycle Model

> **Status**: Proposal
> **Issues**: [#13647](https://github.com/microsoft/aspire/issues/13647), [#10365](https://github.com/microsoft/aspire/issues/10365)
> **Audience**: Aspire contributors, integration authors, and advanced users building custom resources.

## Motivation

Custom resources in Aspire today require significant boilerplate. Authors must manually:

- Construct a full `CustomResourceSnapshot` object with `WithInitialState`
- Write and manage a `while` loop inside `OnInitializeResource`
- Fire lifecycle events (`BeforeResourceStartedEvent`, `ResourceEndpointsAllocatedEvent`) by hand
- Set timestamps (`CreationTimeStamp`, `StartTimeStamp`, `StopTimeStamp`) explicitly
- Wire Start/Stop/Restart dashboard commands (which today only work for DCP-backed resources)
- Work around snapshot clobbering — DCP's `ResourceSnapshotBuilder.ToSnapshot` overwrites `Urls`, `EnvironmentVariables`, `Volumes`, `Relationships`, and some `Properties` on every watch event, silently discarding anything the user added via `PublishUpdateAsync`

This makes custom resources hard to author, error-prone, and fragile. The snapshot clobbering issue (#13647) means users cannot reliably add dynamic URLs or properties to DCP-managed resources.

### The problem in one example

```csharp
// User adds a custom URL to a container.
// It appears briefly, then vanishes when DCP refreshes.
builder.AddContainer("nginx", "nginx")
    .OnBeforeResourceStarted(async (res, evt, ct) =>
    {
        await evt.Services.GetRequiredService<ResourceNotificationService>()
            .PublishUpdateAsync(res, x => x with
            {
                Urls = [.. x.Urls, new("Hello World", "http://localhost/", false)]
            });
    });
```

The "Hello World" URL disappears because `ResourceSnapshotBuilder.ToSnapshot` replaces the entire `Urls` collection with freshly computed DCP-sourced URLs on every state transition.

## Design principles

This proposal is modeled after UI frameworks:

1. **Targeted updates, not wholesale replacement.** Like a UI framework where each component updates its own state without clobbering siblings, each producer (DCP, orchestrator, user code) updates only its slice of the resource snapshot.

2. **Framework owns the lifecycle loop.** Like a UI framework that calls your render/update function rather than you writing a `while` loop, the Aspire framework manages the resource state machine and calls your callbacks at the right time.

3. **Reactive triggers, not imperative loops.** Resource authors declare *what drives their resource* (a timer, another resource's events, an external stream) and provide callbacks. The framework manages when those callbacks fire, including start, stop, and restart.

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│              ResourceNotificationService                    │
│    (merges slices from all producers into one snapshot)     │
│                                                             │
│  ┌────────┐  ┌────────┐  ┌────────┐  ┌────────┐           │
│  │ State  │  │  URLs  │  │  Props │  │  Env   │  ...       │
│  │ slice  │  │ slice  │  │ slice  │  │ slice  │            │
│  └───▲────┘  └───▲────┘  └───▲────┘  └───▲────┘           │
│      │           │           │           │                  │
└──────┼───────────┼───────────┼───────────┼──────────────────┘
       │           │           │           │
  ┌────┴──┐  ┌────┴──┐   ┌────┴──┐  ┌────┴──┐
  │  DCP  │  │Orchest│   │ User  │  │ User  │
  │watcher│  │rator  │   │ code  │  │ code  │
  └───────┘  └───────┘   └───────┘  └───────┘
```

Each producer calls targeted update methods that only affect its own slice. The notification service merges all slices into the final `CustomResourceSnapshot` that flows to the dashboard.

## Layer 1: Source-scoped snapshot updates

### Problem

`ResourceSnapshotBuilder.ToSnapshot` does `previous with { Urls = urls, EnvironmentVariables = env, ... }`, replacing every collection wholesale. Any URL, env var, or property added by user code via `PublishUpdateAsync` is silently lost.

### Solution

The notification service tracks collection items **per source**. Each producer identifies itself when updating a collection. The service stores items grouped by source and merges all sources into the rendered snapshot.

```csharp
// Internal API — used by DCP and the orchestrator:
notifications.PublishUrlsAsync(resource, "dcp", dcpUrls);
notifications.PublishEnvironmentAsync(resource, "dcp", dcpEnvVars);
notifications.PublishPropertiesAsync(resource, "dcp", dcpProperties);

// User code continues to use PublishUpdateAsync.
// Collection writes are routed to the "user" source internally.
await notifications.PublishUpdateAsync(resource, s => s with
{
    Urls = [.. s.Urls, new("Hello World", "http://localhost/", false)]
});
// The "Hello World" URL is stored in the "user" source.
// DCP updates only replace the "dcp" source — user URLs are untouched.
```

When DCP calls `PublishUrlsAsync(resource, "dcp", newUrls)`:
1. All previous URLs from source `"dcp"` are replaced with `newUrls`.
2. URLs from source `"user"` and `"orchestrator"` are untouched.
3. The merged snapshot contains URLs from all sources.

### Atomicity

DCP needs to update state + URLs + env + properties in one atomic operation to avoid intermediate states visible to the dashboard. A batch API handles this:

```csharp
// Internal: atomic multi-slice update
notifications.PublishSlicedUpdateAsync(resource, "dcp", batch =>
{
    batch.State = dcpState;
    batch.Urls = dcpUrls;
    batch.EnvironmentVariables = dcpEnv;
    batch.Properties = dcpProperties;
    batch.Volumes = dcpVolumes;
    batch.Relationships = dcpRelationships;
});
```

### Backward compatibility

`PublishUpdateAsync(resource, Func<CustomResourceSnapshot, CustomResourceSnapshot>)` continues to work exactly as today for scalar fields (`State`, `ExitCode`, timestamps). For collection fields, writes are routed to the `"user"` source. This means existing code that appends to `Urls` via `s => s with { Urls = [..s.Urls, myUrl] }` correctly adds to the user slice without affecting DCP-owned URLs.

## Layer 2: `ResourceContext` and targeted public API

### `ResourceContext`

A new class that provides the state-update surface for resource authors. It's the "component API" — the thing you use to tell the dashboard what to show.

```csharp
public class ResourceContext
{
    /// <summary>The resource this context is associated with.</summary>
    public IResource Resource { get; }

    /// <summary>A logger scoped to this resource.</summary>
    public ILogger Logger { get; }

    /// <summary>The host service provider.</summary>
    public IServiceProvider Services { get; }

    /// <summary>Updates the resource state shown in the dashboard.</summary>
    public Task SetStateAsync(ResourceStateSnapshot state);

    /// <summary>Updates the resource state shown in the dashboard.</summary>
    public Task SetStateAsync(string state);

    /// <summary>Sets a property shown in the resource details panel.</summary>
    public Task SetPropertyAsync(string name, object? value, bool isSensitive = false);

    /// <summary>Adds or updates a URL shown in the dashboard.</summary>
    public Task AddUrlAsync(UrlSnapshot url);

    /// <summary>Removes a URL by name.</summary>
    public Task RemoveUrlAsync(string urlName);

    /// <summary>
    /// Tracks a disposable that will be disposed when the resource stops.
    /// Use this for connections, clients, or other resources with cleanup.
    /// </summary>
    public void Track(IAsyncDisposable disposable);

    /// <summary>Tracks a disposable that will be disposed when the resource stops.</summary>
    public void Track(IDisposable disposable);

    /// <summary>Retrieves a previously tracked object by type.</summary>
    public T Get<T>() where T : class;
}
```

### Targeted methods on `ResourceNotificationService`

In addition to the existing `PublishUpdateAsync`, new public methods for targeted updates:

```csharp
public class ResourceNotificationService
{
    // Existing (kept):
    public Task PublishUpdateAsync(IResource resource,
        Func<CustomResourceSnapshot, CustomResourceSnapshot> stateFactory);

    // New:
    public Task PublishStateAsync(IResource resource, ResourceStateSnapshot state);
    public Task PublishStateAsync(IResource resource, string state);
    public Task PublishPropertyAsync(IResource resource,
        string name, object? value, bool isSensitive = false);
    public Task PublishUrlAsync(IResource resource, UrlSnapshot url);
    public Task RemoveUrlAsync(IResource resource, string urlName);
}
```

### Builder extensions

```csharp
/// <summary>Sets the resource type shown in the dashboard Type column.</summary>
public static IResourceBuilder<T> WithResourceType<T>(
    this IResourceBuilder<T> builder, string resourceType)
    where T : IResource;

/// <summary>Adds a property visible in the dashboard resource details.</summary>
public static IResourceBuilder<T> WithProperty<T>(
    this IResourceBuilder<T> builder,
    string name, object? value, bool isSensitive = false)
    where T : IResource;
```

These are additive — `WithInitialState` remains for backward compatibility but is no longer the recommended way to set resource type and properties.

## Layer 3: Framework-owned lifecycle

### The four lifecycle primitives

Every custom resource is driven by one of four things. The framework provides a primitive for each:

| Driver | Primitive | When to use |
|--------|-----------|-------------|
| Time | `WithInterval(period, onTick)` | Periodic polling, status refresh |
| Another resource | `BoundTo(source)` | Port follows tunnel, sidecar follows container |
| External stream | `RunAsync(execute)` | Streaming connections, subprocess management |
| One-shot | `OnStarted(callback)` | Setup-and-done resources, external services |

All four share the same `ResourceContext` for state updates, and all four get automatic lifecycle management.

### `WithInterval` — framework-owned timer

```csharp
/// <summary>
/// Registers a periodic callback. The framework starts the timer when the resource starts
/// and stops it when the resource stops. No while loop needed.
/// </summary>
public static IResourceBuilder<T> WithInterval<T>(
    this IResourceBuilder<T> builder,
    TimeSpan period,
    Func<ResourceContext, long, Task> onTick)
    where T : IResource;
```

**Example — TalkingClock:**

```csharp
builder.AddResource(new TalkingClockResource("clock"))
    .ExcludeFromManifest()
    .WithResourceType("TalkingClock")
    .WithProperty(CustomResourceKnownProperties.Source, "Talking Clock")
    .WithUrl("https://www.speaking-clock.com/", "Speaking Clock")
    .WithInterval(TimeSpan.FromSeconds(1), async (ctx, tick) =>
    {
        await ctx.SetStateAsync(tick % 2 == 0
            ? new ResourceStateSnapshot("Tick", KnownResourceStateStyles.Success)
            : new ResourceStateSnapshot("Tock", KnownResourceStateStyles.Success));
    });
```

Compare to the [current TalkingClock implementation](../../playground/CustomResources/CustomResources.AppHost/TalkingClockResource.cs) which requires ~30 lines, manual `BeforeResourceStartedEvent` firing, manual timestamps, manual `while` loop, and manual `PublishUpdateAsync` calls.

### `BoundTo` — lifecycle follows another resource

```csharp
/// <summary>
/// Binds this resource's lifecycle to another resource. The framework automatically:
/// - Starts this resource when the source becomes ready
/// - Stops this resource when the source stops
/// - Restarts this resource when the source restarts
/// - Fires all lifecycle events (BeforeResourceStartedEvent, etc.)
/// - Manages timestamps and URL active/inactive state
/// </summary>
public static IResourceBuilder<T> BoundTo<T, TSource>(
    this IResourceBuilder<T> builder,
    IResourceBuilder<TSource> source)
    where T : IResource
    where TSource : IResource;
```

**Example — DevTunnelPort:**

```csharp
// Today: ~130 lines across OnResourceReady + OnResourceStopped handlers
// that manually fire events, set state, allocate endpoints, toggle URL
// activity, manage timestamps, and publish ResourceStoppedEvent.

// Proposed:
portBuilder
    .BoundTo(tunnelBuilder)
    .OnStarted(async ctx =>
    {
        // Framework already: fired BeforeResourceStartedEvent,
        // set Starting, set StartTimeStamp.
        // Do only the port-specific work:
        var port = (DevTunnelPortResource)ctx.Resource;
        var tunnelPort = port.LastKnownStatus!;
        port.TunnelEndpointAnnotation.AllocatedEndpoint =
            new(port.TunnelEndpointAnnotation, tunnelPort.PortUri!.Host, 443);
        // Framework auto-resolves URLs from annotations
    });
    // On tunnel stop: framework sets Stopped, StopTimeStamp,
    // marks URLs inactive, fires ResourceStoppedEvent.
```

#### What `BoundTo` does automatically

1. **Subscribes to source `ResourceReadyEvent`** → starts this resource
2. **Subscribes to source `ResourceStoppedEvent`** → stops this resource
3. **Handles source restart** → stops this resource, then re-starts when source is ready again
4. **Wires Start/Stop/Restart commands** — but Start is gated on source being ready
5. **Fires all lifecycle events** (`BeforeResourceStartedEvent`, `ResourceEndpointsAllocatedEvent`, `ResourceStoppedEvent`)
6. **Manages timestamps** (`StartTimeStamp`, `StopTimeStamp`)
7. **Toggles URL active state** — URLs marked inactive on stop, active on start

### `RunAsync` — author-owned loop (escape hatch)

```csharp
/// <summary>
/// Registers an async function that runs while the resource is started.
/// The stoppingToken is cancelled when the resource should stop.
/// Use this for streaming connections, subprocess management, or any case
/// where the resource needs a long-running loop.
/// </summary>
public static IResourceBuilder<T> RunAsync<T>(
    this IResourceBuilder<T> builder,
    Func<ResourceContext, CancellationToken, Task> execute)
    where T : IResource;
```

**Example — Kafka monitor:**

```csharp
builder.AddResource(new KafkaMonitorResource("kafka-monitor"))
    .WithResourceType("KafkaMonitor")
    .RunAsync(async (ctx, stoppingToken) =>
    {
        var consumer = new KafkaConsumer(ctx.Get<KafkaConfig>().BootstrapServers);
        ctx.Track(consumer);

        await foreach (var msg in consumer.ConsumeAsync(stoppingToken))
        {
            await ctx.SetPropertyAsync("last-offset", msg.Offset);
            await ctx.SetPropertyAsync("last-timestamp", msg.Timestamp);
        }
    });
```

### `OnStarted` / `OnStopping` — one-shot setup and cleanup

```csharp
/// <summary>
/// Registers a callback that runs once when the resource starts.
/// Use this for setup that doesn't require a long-running loop.
/// </summary>
public static IResourceBuilder<T> OnStarted<T>(
    this IResourceBuilder<T> builder,
    Func<ResourceContext, Task> onStarted)
    where T : IResource;

/// <summary>
/// Registers a callback that runs when the resource is stopping.
/// Use this for cleanup.
/// </summary>
public static IResourceBuilder<T> OnStopping<T>(
    this IResourceBuilder<T> builder,
    Func<ResourceContext, Task> onStopping)
    where T : IResource;
```

**Example — external service:**

```csharp
builder.AddResource(new ExternalApiResource("payments-api"))
    .WithResourceType("ExternalAPI")
    .WithUrl("https://api.payments.com", "API")
    .OnStarted(async ctx =>
    {
        var client = new HttpClient();
        ctx.Track(client);
        var response = await client.GetAsync("https://api.payments.com/health");
        await ctx.SetStateAsync(response.IsSuccessStatusCode ? "Healthy" : "Degraded");
    });
```

### What the framework handles automatically

For all four primitives, the framework provides:

| Concern | What the framework does |
|---------|------------------------|
| **State machine** | Manages NotStarted → Starting → Running → Stopping → Stopped transitions |
| **Timestamps** | Sets `CreationTimeStamp` at registration, `StartTimeStamp` on start, `StopTimeStamp` on stop |
| **Lifecycle events** | Fires `BeforeResourceStartedEvent` before calling callbacks; fires `ResourceStoppedEvent` on stop |
| **Dashboard commands** | Wires Start/Stop/Restart commands automatically |
| **URL resolution** | Auto-resolves `ResourceUrlAnnotation`s into `UrlSnapshot`s |
| **URL active state** | Marks URLs inactive on stop, active on start |
| **`WaitFor` support** | Works automatically because `BeforeResourceStartedEvent` fires at the right time |
| **Error handling** | If a callback throws (not `OperationCanceledException`), state → FailedToStart, logged, restartable from dashboard |
| **Cleanup** | All tracked disposables (`ctx.Track(...)`) are disposed on stop |

### Start/Stop/Restart routing for custom resources

Today, `ApplicationOrchestrator.StartResourceAsync` and `StopResourceAsync` route directly to `_dcpExecutor`, which only knows about DCP-managed resources. Custom resources get nothing.

With this proposal, the orchestrator gains a custom resource lifecycle path:

```
Dashboard "Stop" click
  → CommandsConfigurationExtensions
    → orchestrator.StopResourceAsync(name)
      → If DCP resource → _dcpExecutor.StopResourceAsync (existing path)
      → If custom resource → cancel the resource's CancellationTokenSource
        → WithInterval: timer stopped
        → BoundTo: unbind from source events
        → RunAsync: stoppingToken cancelled, await completion
        → OnStopping: called
        → Framework: state = Stopped, StopTimeStamp set, URLs inactive

Dashboard "Start" click
  → orchestrator.StartResourceAsync(name)
    → If DCP resource → _dcpExecutor.StartResourceAsync (existing path)
    → If custom resource → create new CTS, invoke lifecycle callbacks
      → BoundTo: re-subscribe, but gate on source being ready
      → Framework: state = Starting → Running
```

## TypeScript API

The TypeScript API mirrors the C# API through the polyglot code-generation system.

### `ResourceNotificationService`

```typescript
export interface ResourceNotificationService {
    // Existing:
    publishResourceUpdate(resource: Awaitable<Resource>,
        options?: PublishResourceUpdateOptions): ResourceNotificationServicePromise;

    // New:
    publishState(resource: Awaitable<Resource>,
        state: string,
        options?: PublishStateOptions): ResourceNotificationServicePromise;

    publishProperty(resource: Awaitable<Resource>,
        name: string, value: unknown,
        options?: PublishPropertyOptions): ResourceNotificationServicePromise;

    publishUrl(resource: Awaitable<Resource>,
        url: UrlSnapshot): ResourceNotificationServicePromise;

    removeUrl(resource: Awaitable<Resource>,
        urlName: string): ResourceNotificationServicePromise;
}

export interface PublishStateOptions {
    stateStyle?: string;
}

export interface PublishPropertyOptions {
    isSensitive?: boolean;
}

export interface UrlSnapshot {
    name?: string;
    url: string;
    isInternal?: boolean;
}
```

### `InitializeResourceEvent`

```typescript
export interface InitializeResourceEvent {
    // Existing:
    resource(): Promise<Resource>;
    eventing(): Promise<IDistributedApplicationEventing>;
    logger(): Promise<ILogger>;
    notifications(): Promise<ResourceNotificationService>;
    services(): Promise<IServiceProvider>;

    // New:
    setStarted(): Promise<void>;
    setState(state: string, options?: PublishStateOptions): Promise<void>;
    setProperty(name: string, value: unknown,
        options?: PublishPropertyOptions): Promise<void>;
    addUrl(url: UrlSnapshot): Promise<void>;
}
```

### Builder extensions

```typescript
// On any resource builder:
.withResourceType(resourceType: string)
.withProperty(name: string, value: unknown, options?: PublishPropertyOptions)
.withInterval(periodMs: number,
    onTick: (ctx: ResourceContext, tick: number) => Promise<void>)
.boundTo(source: Awaitable<Resource>)
.onStarted(callback: (ctx: ResourceContext) => Promise<void>)
.onStopping(callback: (ctx: ResourceContext) => Promise<void>)
.run(execute: (ctx: ResourceContext, stoppingToken: AbortSignal) => Promise<void>)
```

### TypeScript example

```typescript
// TalkingClock
const clock = await builder.addResource("clock")
    .withResourceType("TalkingClock")
    .withProperty("aspire.resource.source", "Talking Clock")
    .withInterval(1000, async (ctx, tick) => {
        await ctx.setState(tick % 2 === 0 ? "Tick" : "Tock",
            { stateStyle: "success" });
    });

// Bound lifecycle
const port = await builder.addResource("tunnel-port")
    .withResourceType("DevTunnelPort")
    .boundTo(tunnel)
    .onStarted(async (ctx) => {
        await ctx.addUrl({ name: "tunnel", url: tunnelPortUrl });
    });
```

## Composition rules

The lifecycle primitives compose as follows:

| Combination | Behavior |
|-------------|----------|
| Multiple `WithInterval` | All timers run independently |
| `WithInterval` + `OnStarted` | `OnStarted` fires first, then timers start |
| `WithInterval` + `OnStopping` | Timers stop first, then `OnStopping` fires |
| `BoundTo` + `OnStarted` | `OnStarted` fires when the source becomes ready |
| `BoundTo` + `OnStopping` | `OnStopping` fires when the source stops |
| `RunAsync` | Exclusive — cannot combine with `WithInterval` or `BoundTo` (it IS the lifecycle) |
| `RunAsync` + `OnStopping` | `OnStopping` fires after `RunAsync` returns |
| Multiple `OnStarted` | All fire in registration order |
| Multiple `OnStopping` | All fire in reverse registration order |

## Error handling

| Scenario | Behavior |
|----------|----------|
| `OnStarted` throws | State → `FailedToStart`, error logged. Restartable from dashboard. |
| `WithInterval` tick throws | Error logged, tick skipped. Timer continues. After N consecutive failures (configurable), state → `Unhealthy`. |
| `RunAsync` throws (not `OperationCanceledException`) | State → `FailedToStart`, error logged. Restartable from dashboard. |
| `RunAsync` catches `OperationCanceledException` | Normal stop. State → `Stopped`. |
| `OnStopping` throws | Error logged. Stop proceeds — resource still transitions to Stopped. |

## Migration guide

### From `WithInitialState` + `OnInitializeResource`

```csharp
// Before:
builder.AddResource(myResource)
    .WithInitialState(new CustomResourceSnapshot
    {
        ResourceType = "MyResource",
        CreationTimeStamp = DateTime.UtcNow,
        State = KnownResourceStates.NotStarted,
        Properties = [new("Source", "my-source")]
    })
    .OnInitializeResource(async (resource, @event, token) =>
    {
        await @event.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(resource, @event.Services), token);
        await @event.Notifications.PublishUpdateAsync(resource, s => s with
        {
            StartTimeStamp = DateTime.UtcNow,
            State = KnownResourceStates.Running
        });
        while (!token.IsCancellationRequested)
        {
            // ... work ...
            await Task.Delay(interval, token);
        }
    });

// After:
builder.AddResource(myResource)
    .WithResourceType("MyResource")
    .WithProperty("Source", "my-source")
    .WithInterval(interval, async (ctx, tick) =>
    {
        // ... work ...
    });
```

### From `OnResourceReady` / `OnResourceStopped` (bound lifecycle)

```csharp
// Before:
parentBuilder.OnResourceReady(async (parent, e, ct) =>
{
    var notifications = e.Services.GetRequiredService<ResourceNotificationService>();
    var eventing = e.Services.GetRequiredService<IDistributedApplicationEventing>();
    await eventing.PublishAsync<BeforeResourceStartedEvent>(
        new(childResource, e.Services), EventDispatchBehavior.NonBlockingSequential, ct);
    await notifications.PublishUpdateAsync(childResource, s => s with
    {
        State = KnownResourceStates.Starting,
        StartTimeStamp = DateTime.UtcNow
    });
    // ... setup ...
    await notifications.PublishUpdateAsync(childResource, s => s with
    {
        State = KnownResourceStates.Running
    });
});
parentBuilder.OnResourceStopped(async (parent, e, ct) =>
{
    var notifications = e.Services.GetRequiredService<ResourceNotificationService>();
    await notifications.PublishUpdateAsync(childResource, s => s with
    {
        State = KnownResourceStates.Finished,
        StopTimeStamp = DateTime.UtcNow,
        Urls = [.. s.Urls.Select(u => u with { IsInactive = true })]
    });
});

// After:
childBuilder
    .BoundTo(parentBuilder)
    .OnStarted(async ctx =>
    {
        // ... setup (just the custom part) ...
    });
```

## Implementation phases

| Phase | Scope | Dependencies |
|-------|-------|-------------|
| **1. Source-scoped slices** | Internal plumbing: per-source collection tracking in `ResourceNotificationService`, `PublishSlicedUpdateAsync` for DCP, refactor `ResourceSnapshotBuilder` and `ApplicationOrchestrator`. Fixes #13647. | None |
| **2. `ResourceContext` + public API** | New `ResourceContext` class, targeted public methods on `ResourceNotificationService`, builder helpers (`WithResourceType`, `WithProperty`), `CreationTimeStamp` auto-default. Improves #10365 ergonomics. | Phase 1 |
| **3. Lifecycle host + triggers** | `WithInterval`, `BoundTo`, `OnStarted`, `OnStopping`, `RunAsync`. Framework state machine, auto-events, auto-commands, Start/Stop routing for custom resources. | Phase 2 |
| **4. TypeScript parity** | Code-gen changes to emit new methods on `ResourceNotificationService`, `InitializeResourceEvent`, and resource builders. | Phase 3 |

Each phase is independently shippable and useful. Phase 1 alone fixes the clobbering bug. Phase 2 alone improves ergonomics. Phase 3 eliminates boilerplate.

## Open questions

1. **`RunAsync` exclusivity.** Should `RunAsync` be mutually exclusive with `WithInterval` and `BoundTo`, or should they compose? Recommendation: exclusive — `RunAsync` IS the lifecycle, and combining it with a timer or binding creates ambiguity about who owns the state machine.

2. **`WithInterval` error threshold.** How many consecutive tick failures before the resource is marked unhealthy? A configurable default (e.g., 3) with an override parameter seems right.

3. **`BoundTo` vs `WaitFor`.** `BoundTo(source)` means "my lifecycle follows the source." `WaitFor(source)` means "don't start me until the source is ready, but I have my own lifecycle." These are distinct and both useful. `BoundTo` implies `WaitFor`, but `WaitFor` does not imply `BoundTo`.

4. **`PublishUpdateAsync` collection semantics.** When existing code does `s => s with { Urls = [myUrl] }`, should this replace only the `"user"` source's URLs (breaking: user loses all other URLs from `s.Urls` they might have appended), or set the full merged collection (non-breaking but clobbers DCP URLs)? Recommendation: write to `"user"` source only, and log a warning when collection fields are set via the legacy API to guide migration.

5. **`ResourceContext` lifetime on restart.** On restart, should a fresh `ResourceContext` be created (all tracked disposables disposed, fresh state)? Recommendation: yes — restart is a full stop + start cycle.

6. **Naming.** `BoundTo` vs `FollowLifecycle` vs `DependsOn`? `RunAsync` vs `WithLifecycle` vs `ExecuteAsync`? `OnStarted` vs `OnStart`? Naming TBD pending API review.
