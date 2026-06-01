// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Templating;

/// <summary>
/// Path-segment helpers shared by embedded-template renderers.
/// </summary>
internal static class EmbeddedTemplatePathHelpers
{
    /// <summary>
    /// Rewrites the trailing <c>._csproj</c> / <c>._fsproj</c> / <c>._vbproj</c> source
    /// extension on a path segment to the real project extension on output. The source
    /// trees use the underscore-prefixed form so the repo-wide MSBuild traversal in
    /// <c>eng/Build.props</c> does not pick them up as real projects and try to resolve
    /// the unsubstituted <c>Aspire.AppHost.Sdk/{{aspireVersion}}</c> reference.
    /// </summary>
    public static string RewriteTemplateProjectExtension(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);

        if (segment.EndsWith("._csproj", StringComparison.Ordinal))
        {
            return string.Concat(segment.AsSpan(0, segment.Length - "._csproj".Length), ".csproj");
        }

        if (segment.EndsWith("._fsproj", StringComparison.Ordinal))
        {
            return string.Concat(segment.AsSpan(0, segment.Length - "._fsproj".Length), ".fsproj");
        }

        if (segment.EndsWith("._vbproj", StringComparison.Ordinal))
        {
            return string.Concat(segment.AsSpan(0, segment.Length - "._vbproj".Length), ".vbproj");
        }

        return segment;
    }
}
