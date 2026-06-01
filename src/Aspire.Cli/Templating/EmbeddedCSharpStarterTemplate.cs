// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Aspire.Cli.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Templating;

/// <summary>
/// Renders the embedded C# multi-project "starter" template
/// (AppHost + ApiService + Web + ServiceDefaults — no Tests project, no .sln,
/// no <c>.template.config</c>) into an output directory. This is the
/// embedded-engine replacement for the previous flow that resolved the
/// <c>Aspire.ProjectTemplates</c> NuGet package, installed it via
/// <c>dotnet new install</c>, and then invoked <c>dotnet new aspire-starter</c>.
/// </summary>
/// <remarks>
/// Symbol translation from the upstream <c>aspire-starter</c>
/// <c>template.json</c> (the source of truth for layout; this template was
/// pre-processed at embed time to fold static conditionals — see the docs at
/// the top of the embedded source tree):
/// <list type="bullet">
/// <item><c>sourceName: Aspire-StarterApplication.1</c> → <c>{{projectName}}</c>
/// token, applied to both file contents and the <c>{{projectName}}.*._csproj</c>
/// filenames via the path transformer.</item>
/// <item><c>GeneratedClassNamePrefix</c> regex symbol → <c>{{generatedClassNamePrefix}}</c>;
/// computed in C# via <see cref="ComputeGeneratedClassNamePrefix"/> which mirrors
/// the upstream regex <c>(((?&lt;=\.)|^)(?=\d)|\W)</c> → <c>_</c>.</item>
/// <item><c>XmlEncodedProjectName</c> derived xmlEncode symbol → <c>{{projectName}}</c>
/// directly. The CLI rejects project names containing characters that need
/// XML-encoding before reaching the renderer, so the rendered csproj
/// <c>&lt;ProjectReference&gt;</c> includes are safe with the raw name.</item>
/// <item><c>Framework</c> choice → hardcoded <c>net10.0</c>. All
/// <c>Framework == 'net8.0'</c> branches were folded out at embed time; the
/// <c>Microsoft.AspNetCore.OpenApi</c> net9 reference was dropped.</item>
/// <item><c>HasHttpsProfile</c> computed symbol → hardcoded <c>true</c>;
/// matches the AppHost-only embedded template behavior.</item>
/// <item><c>UseRedisCache</c> bool → <c>{{#useRedisCache}}</c> /
/// <c>{{^useRedisCache}}</c> Mustache blocks in AppHost.cs / Web Program.cs /
/// AppHost csproj / Web csproj.</item>
/// <item><c>LocalhostTld</c> bool → <c>{{#localhostTld}}</c> / <c>{{^localhostTld}}</c>
/// blocks in launchSettings.json files. The <c>hostName</c> derived symbol
/// (<c>lowerCaseInvariantWithHyphens</c> form chain) is computed in C# via
/// <see cref="EmbeddedCSharpAppHostTemplate.ComputeLocalhostTldHostName"/>
/// and surfaced as <c>{{hostName}}</c>.</item>
/// <item><c>UserSecretsId</c> → freshly generated GUID per template
/// application, matching the upstream built-in GUID symbol.</item>
/// <item>The 12 <c>port</c> + <c>coalesce</c> generators → resolved by
/// <see cref="AppHostProfilePortGenerator"/> (the six AppHost ports) and
/// <see cref="StarterProfilePortGenerator"/> (the four Web and ApiService
/// ports) ahead of time and injected as their respective tokens.</item>
/// <item><c>TestFx</c> / Tests project / <c>.sln</c> / <c>.template.config</c>
/// → dropped. The CLI does not surface a test framework choice today and the
/// other artifacts have no consumer outside <c>dotnet new</c>.</item>
/// <item>Package versions (<c>!!REPLACE_WITH_*!!</c>) →
/// <see cref="EmbeddedTemplatePackageVersions"/> for the OTel / Extensions
/// versions baked into Aspire.Cli at build time, plus
/// <see cref="VersionHelper.GetDefaultTemplateVersion"/> for Aspire's own
/// version (SDK reference, Aspire.Hosting.Redis, Aspire.StackExchange.Redis.OutputCaching).</item>
/// </list>
/// </remarks>
internal static class EmbeddedCSharpStarterTemplate
{
    // Mirrors the "safe namespace" form the .NET template engine derives from
    // `sourceName` for the upstream `aspire-starter` template — the form that
    // preserves '.' (so namespace segments stay intact), replaces every other
    // non-`[A-Za-z0-9_.]` character with `_`, and prefixes `_` to any digit
    // that starts a segment (start-of-string or immediately after `.`).
    //
    // The upstream template.json `GeneratedClassNamePrefix` regex symbol
    // `(((?<=\.)|^)(?=\d)|\W)` does NOT preserve `.` on its own (because `.`
    // matches `\W`); the template engine compensates by also auto-deriving the
    // safe-namespace form of `sourceName` and using it as the placeholder text
    // in the source files (e.g. `Aspire_StarterApplication._1.Web` in
    // _Imports.razor). We bake both into a single token, so the C# regex below
    // is the safe-namespace form directly.
    //
    // Examples:
    //   MyApp                        -> MyApp
    //   My-App                       -> My_App
    //   My.App.1                     -> My.App._1
    //   1MyApp                       -> _1MyApp
    //   Aspire-StarterApplication.1  -> Aspire_StarterApplication._1
    private static readonly Regex s_generatedClassNamePrefixRegex = new(
        @"((^|(?<=\.))(?=\d)|[^A-Za-z0-9_.])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Writes the embedded C# multi-project starter template into
    /// <paramref name="outputPath"/>. The output directory is created if it
    /// does not already exist; existing files are overwritten.
    /// </summary>
    public static async Task RenderAsync(
        string outputPath,
        string projectName,
        bool useRedisCache,
        bool useLocalhostTld,
        string? templateVersion,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        ArgumentNullException.ThrowIfNull(logger);

        var aspireVersion = string.IsNullOrWhiteSpace(templateVersion)
            ? VersionHelper.GetDefaultTemplateVersion()
            : templateVersion;
        var appHostPorts = AppHostProfilePortGenerator.Generate(Random.Shared);
        var starterPorts = StarterProfilePortGenerator.Generate(Random.Shared);
        var userSecretsId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
        var hostName = EmbeddedCSharpAppHostTemplate.ComputeLocalhostTldHostName(projectName);
        var generatedClassNamePrefix = ComputeGeneratedClassNamePrefix(projectName);

        var conditions = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["useRedisCache"] = useRedisCache,
            ["localhostTld"] = useLocalhostTld
        };

        string ApplyContentTransform(string content)
        {
            var tokensApplied = ApplyTokens(
                content,
                projectName,
                generatedClassNamePrefix,
                aspireVersion,
                appHostPorts,
                starterPorts,
                hostName,
                userSecretsId);
            return ConditionalBlockProcessor.Process(tokensApplied, conditions);
        }

        // See EmbeddedCSharpAppHostTemplate for the rationale on splitting the
        // path transform from the content transform (Mustache conditionals can
        // only live inside file content, not in path segments).
        string ApplyPathTransform(string segment)
        {
            var tokensApplied = ApplyTokens(
                segment,
                projectName,
                generatedClassNamePrefix,
                aspireVersion,
                appHostPorts,
                starterPorts,
                hostName,
                userSecretsId);
            return EmbeddedTemplatePathHelpers.RewriteTemplateProjectExtension(tokensApplied);
        }

        logger.LogDebug(
            "Rendering embedded C# starter template to '{OutputPath}' (project '{ProjectName}', Aspire version '{AspireVersion}', UseRedisCache={UseRedisCache}, LocalhostTld={LocalhostTld}).",
            outputPath,
            projectName,
            aspireVersion,
            useRedisCache,
            useLocalhostTld);

        if (!Directory.Exists(outputPath))
        {
            Directory.CreateDirectory(outputPath);
        }

        var source = new EmbeddedResourceTemplateSource(typeof(EmbeddedCSharpStarterTemplate).Assembly, "csharp-starter");
        var renderer = new TemplateRenderer(logger);
        await renderer.RenderAsync(source, outputPath, ApplyContentTransform, cancellationToken, ApplyPathTransform);
    }

    /// <summary>
    /// Computes the C# class-name-safe prefix for namespaces and generated
    /// type names (e.g. <c>Projects.{Prefix}_AppHost</c>) using the same regex
    /// the upstream <c>aspire-starter</c> template applies. Examples:
    /// <c>Aspire-StarterApplication.1</c> → <c>Aspire_StarterApplication._1</c>;
    /// <c>1MyApp</c> → <c>_1MyApp</c>.
    /// </summary>
    internal static string ComputeGeneratedClassNamePrefix(string projectName)
    {
        return s_generatedClassNamePrefixRegex.Replace(projectName, "_");
    }

    private static string ApplyTokens(
        string content,
        string projectName,
        string generatedClassNamePrefix,
        string aspireVersion,
        AppHostProfilePorts appHostPorts,
        StarterProfilePorts starterPorts,
        string hostName,
        string userSecretsId)
    {
        var sb = new StringBuilder(content);

        sb.Replace("{{projectName}}", projectName);
        sb.Replace("{{generatedClassNamePrefix}}", generatedClassNamePrefix);
        sb.Replace("{{aspireVersion}}", aspireVersion);
        sb.Replace("{{userSecretsId}}", userSecretsId);
        sb.Replace("{{hostName}}", hostName);

        sb.Replace("{{appHostHttpPort}}", appHostPorts.DashboardHttpPort.ToString(CultureInfo.InvariantCulture));
        sb.Replace("{{appHostHttpsPort}}", appHostPorts.DashboardHttpsPort.ToString(CultureInfo.InvariantCulture));
        sb.Replace("{{appHostOtlpHttpPort}}", appHostPorts.OtlpHttpPort.ToString(CultureInfo.InvariantCulture));
        sb.Replace("{{appHostOtlpHttpsPort}}", appHostPorts.OtlpHttpsPort.ToString(CultureInfo.InvariantCulture));
        sb.Replace("{{appHostResourceHttpPort}}", appHostPorts.ResourceServiceHttpPort.ToString(CultureInfo.InvariantCulture));
        sb.Replace("{{appHostResourceHttpsPort}}", appHostPorts.ResourceServiceHttpsPort.ToString(CultureInfo.InvariantCulture));

        sb.Replace("{{webHttpPort}}", starterPorts.WebHttpPort.ToString(CultureInfo.InvariantCulture));
        sb.Replace("{{webHttpsPort}}", starterPorts.WebHttpsPort.ToString(CultureInfo.InvariantCulture));
        sb.Replace("{{apiServiceHttpPort}}", starterPorts.ApiServiceHttpPort.ToString(CultureInfo.InvariantCulture));
        sb.Replace("{{apiServiceHttpsPort}}", starterPorts.ApiServiceHttpsPort.ToString(CultureInfo.InvariantCulture));

        // The four package-version tokens consumed by the embedded starter
        // template come from AssemblyMetadata baked into Aspire.Cli at build
        // time (see EmbeddedTemplatePackageVersions / Aspire.Cli.csproj).
        sb.Replace("{{microsoftExtensionsHttpResilienceVersion}}", EmbeddedTemplatePackageVersions.MicrosoftExtensionsHttpResilienceVersion);
        sb.Replace("{{microsoftExtensionsServiceDiscoveryVersion}}", EmbeddedTemplatePackageVersions.MicrosoftExtensionsServiceDiscoveryVersion);
        sb.Replace("{{openTelemetryExporterVersion}}", EmbeddedTemplatePackageVersions.OpenTelemetryExporterOpenTelemetryProtocolVersion);
        sb.Replace("{{openTelemetryHostingVersion}}", EmbeddedTemplatePackageVersions.OpenTelemetryInstrumentationExtensionsHostingVersion);
        sb.Replace("{{openTelemetryInstrumentationAspNetCoreVersion}}", EmbeddedTemplatePackageVersions.OpenTelemetryInstrumentationAspNetCoreVersion);
        sb.Replace("{{openTelemetryInstrumentationHttpVersion}}", EmbeddedTemplatePackageVersions.OpenTelemetryInstrumentationHttpVersion);
        sb.Replace("{{openTelemetryInstrumentationRuntimeVersion}}", EmbeddedTemplatePackageVersions.OpenTelemetryInstrumentationRuntimeVersion);
        sb.Replace("{{aspNetCoreOpenApi10Version}}", EmbeddedTemplatePackageVersions.MicrosoftAspNetCoreOpenApiPreviewVersion);

        return sb.ToString();
    }
}
