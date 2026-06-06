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
        var activeResourceNameByResourceKey = new Dictionary<ResourceKey, string>(activeResources.Count * 2);
        foreach (var resource in activeResources)
        {
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
