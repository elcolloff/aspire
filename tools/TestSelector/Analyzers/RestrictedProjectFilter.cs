// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace TestSelector.Analyzers;

/// <summary>
/// Applies the <c>restrictedTestProjects</c> rule: a project on the restricted list
/// runs ONLY when an explicit <c>sourceToTestMappings</c> entry resolved to it.
/// </summary>
/// <remarks>
/// This is intentionally a free function so it can be unit-tested in isolation.
/// <see cref="TestEvaluator"/> calls it from inside
/// <c>FilterAndCombineTestProjects</c> after merging mapping + dotnet-affected
/// results. The semantics:
/// <list type="bullet">
///   <item>
///     A restricted project pulled in only by dotnet-affected (transitive
///     reference, package dependency) is dropped — the user opt-in is the
///     explicit mapping, not the implicit reference graph.
///   </item>
///   <item>
///     A restricted project resolved by a <c>sourceToTestMappings</c> entry
///     is kept — that mapping is the explicit opt-in.
///   </item>
///   <item>
///     Non-restricted projects pass through unchanged.
///   </item>
/// </list>
/// </remarks>
public static class RestrictedProjectFilter
{
    /// <summary>
    /// Returns <paramref name="allTestProjects"/> with restricted-but-not-mapping-resolved
    /// projects removed. Comparisons are case-insensitive and tolerate backslash separators.
    /// </summary>
    /// <param name="allTestProjects">Merged test project list (mapping + dotnet-affected).</param>
    /// <param name="restrictedProjects">Paths from <c>restrictedTestProjects</c>; empty means no-op.</param>
    /// <param name="mappingResolvedProjects">Projects resolved by <c>sourceToTestMappings</c>.</param>
    /// <returns>Filtered list preserving original order.</returns>
    public static List<string> Apply(
        IReadOnlyList<string> allTestProjects,
        IReadOnlyList<string> restrictedProjects,
        IReadOnlyList<string> mappingResolvedProjects)
    {
        if (restrictedProjects.Count == 0)
        {
            return allTestProjects.ToList();
        }

        var restricted = new HashSet<string>(
            restrictedProjects.Select(Normalize),
            StringComparer.OrdinalIgnoreCase);
        var mapped = new HashSet<string>(
            mappingResolvedProjects.Select(Normalize),
            StringComparer.OrdinalIgnoreCase);

        return allTestProjects
            .Where(p =>
            {
                var n = Normalize(p);
                return !restricted.Contains(n) || mapped.Contains(n);
            })
            .ToList();
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}
