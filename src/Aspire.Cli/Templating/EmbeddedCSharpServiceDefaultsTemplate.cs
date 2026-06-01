// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Templating;

/// <summary>
/// Renders the embedded C# ServiceDefaults class library template
/// (<c>{{projectName}}.csproj</c> + <c>Extensions.cs</c>) into an output
/// directory. This is the embedded-engine replacement for the previous flow
/// that resolved the <c>Aspire.ProjectTemplates</c> NuGet package, installed
/// it via <c>dotnet new install</c>, and then invoked
/// <c>dotnet new aspire-servicedefaults</c>.
/// </summary>
/// <remarks>
/// Symbol translation from the standalone <c>aspire-servicedefaults</c>
/// <c>template.json</c>:
/// <list type="bullet">
/// <item><c>sourceName: Aspire.ServiceDefaults1</c> → <c>{{projectName}}</c>
/// token (applied to both file contents and the
/// <c>{{projectName}}._csproj</c> filename via the path transformer; the
/// <c>._csproj</c> source extension is rewritten to <c>.csproj</c> on
/// output).</item>
/// <item><c>Framework</c> choice → <c>{{targetFramework}}</c> token (defaults
/// to <c>net10.0</c> when the caller does not pass an explicit framework).
/// The standalone NuGet template offers a choice between net8.0/net9.0/net10.0
/// for <c>dotnet new</c> consumers; the embedded path will narrow this once
/// <c>aspire new aspire-servicedefaults</c> is wired up.</item>
/// <item><c>!!REPLACE_WITH_*_VERSION!!</c> placeholders → <c>{{packageVersion}}</c>
/// tokens resolved from <see cref="EmbeddedTemplatePackageVersions"/>. The
/// standalone NuGet template patches these strings at pack time
/// (<c>Aspire.ProjectTemplates.csproj</c> &lt;Replacements&gt;); the
/// embedded path bakes the same property values in via
/// <see cref="System.Reflection.AssemblyMetadataAttribute"/>.</item>
/// <item><c>restore</c> post-action → dropped. <c>aspire run</c> /
/// <c>aspire restore</c> already cover first-use restore for the embedded
/// path.</item>
/// </list>
/// </remarks>
internal static class EmbeddedCSharpServiceDefaultsTemplate
{
    internal const string DefaultTargetFramework = "net10.0";

    /// <summary>
    /// Writes the embedded C# ServiceDefaults template into
    /// <paramref name="outputPath"/>. The output directory is created if it
    /// does not already exist; existing files are overwritten.
    /// </summary>
    public static async Task RenderAsync(
        string outputPath,
        string projectName,
        ILogger logger,
        CancellationToken cancellationToken,
        string? targetFramework = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        ArgumentNullException.ThrowIfNull(logger);

        var framework = string.IsNullOrWhiteSpace(targetFramework)
            ? DefaultTargetFramework
            : targetFramework;

        string ApplyTokens(string value)
        {
            return value
                .Replace("{{projectName}}", projectName)
                .Replace("{{targetFramework}}", framework)
                .Replace("{{microsoftExtensionsHttpResilienceVersion}}", EmbeddedTemplatePackageVersions.MicrosoftExtensionsHttpResilienceVersion)
                .Replace("{{microsoftExtensionsServiceDiscoveryVersion}}", EmbeddedTemplatePackageVersions.MicrosoftExtensionsServiceDiscoveryVersion)
                .Replace("{{openTelemetryExporterVersion}}", EmbeddedTemplatePackageVersions.OpenTelemetryExporterOpenTelemetryProtocolVersion)
                .Replace("{{openTelemetryHostingVersion}}", EmbeddedTemplatePackageVersions.OpenTelemetryInstrumentationExtensionsHostingVersion)
                .Replace("{{openTelemetryInstrumentationAspNetCoreVersion}}", EmbeddedTemplatePackageVersions.OpenTelemetryInstrumentationAspNetCoreVersion)
                .Replace("{{openTelemetryInstrumentationHttpVersion}}", EmbeddedTemplatePackageVersions.OpenTelemetryInstrumentationHttpVersion)
                .Replace("{{openTelemetryInstrumentationRuntimeVersion}}", EmbeddedTemplatePackageVersions.OpenTelemetryInstrumentationRuntimeVersion);
        }

        // Embedded source files use the `._csproj` extension instead of `.csproj`
        // so the repo-wide MSBuild traversal (eng/Build.props) does not pick them
        // up as real projects. The path transform restores `.csproj` on output.
        string ApplyPathTransform(string segment)
        {
            var tokensApplied = ApplyTokens(segment);
            return EmbeddedTemplatePathHelpers.RewriteTemplateProjectExtension(tokensApplied);
        }

        logger.LogDebug(
            "Rendering embedded C# ServiceDefaults template to '{OutputPath}' (project '{ProjectName}', target framework '{TargetFramework}').",
            outputPath,
            projectName,
            framework);

        if (!Directory.Exists(outputPath))
        {
            Directory.CreateDirectory(outputPath);
        }

        var source = new EmbeddedResourceTemplateSource(typeof(EmbeddedCSharpServiceDefaultsTemplate).Assembly, "csharp-servicedefaults");
        var renderer = new TemplateRenderer(logger);
        await renderer.RenderAsync(source, outputPath, ApplyTokens, cancellationToken, ApplyPathTransform);
    }
}
