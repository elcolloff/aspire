// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using Aspire.Dashboard.Otlp.Storage;

namespace Aspire.Dashboard.Model.ResourceGraph;

internal sealed class TelemetryGraphResources
{
    private TelemetryGraphResources(int resourceCapacity, int edgeCapacity)
    {
        ReferencedNames = new Dictionary<string, HashSet<string>>(Math.Min(resourceCapacity, edgeCapacity), StringComparers.ResourceName);
        ResourceNames = new HashSet<string>(resourceCapacity, StringComparers.ResourceName);
    }

    public Dictionary<string, HashSet<string>> ReferencedNames { get; }
    public HashSet<string> ResourceNames { get; }

    public static TelemetryGraphResources Create(IReadOnlyList<ResourceViewModel> activeResources, IReadOnlyList<TelemetryGraphEdge> edgeKeys)
    {
        // Keep this projection scoped to a single graph refresh. The persistent telemetry edge index is
        // bounded by retained traces; this avoids adding another server-side cache with a separate lifetime.
        var activeResourceNameByResourceKey = new Dictionary<ResourceKey, string>(activeResources.Count * 2);
        foreach (var resource in activeResources)
        {
            // Telemetry edges can come from instrumented OTLP resources or from uninstrumented peer
            // resolution. Instrumented resources usually normalize "{DisplayName}-{replica id}" via
            // ResourceKey.Create, while peer resolution can carry the dashboard resource name as the
            // instance id. Index both forms so telemetry flow still resolves after either path produced it.
            activeResourceNameByResourceKey[ResourceKey.Create(resource.DisplayName, resource.Name)] = resource.Name;
            activeResourceNameByResourceKey[new ResourceKey(resource.DisplayName, resource.Name)] = resource.Name;
        }

        var result = new TelemetryGraphResources(activeResources.Count, edgeKeys.Count);
        foreach (var edge in edgeKeys)
        {
            if (!activeResourceNameByResourceKey.TryGetValue(edge.Source, out var sourceName) ||
                !activeResourceNameByResourceKey.TryGetValue(edge.Destination, out var destinationName) ||
                string.Equals(sourceName, destinationName, StringComparisons.ResourceName))
            {
                continue;
            }

            ref var names = ref CollectionsMarshal.GetValueRefOrAddDefault(result.ReferencedNames, sourceName, out _);
            names ??= new HashSet<string>(StringComparers.ResourceName);
            if (names.Add(destinationName))
            {
                result.ResourceNames.Add(sourceName);
                result.ResourceNames.Add(destinationName);
            }
        }

        return result;
    }
}
