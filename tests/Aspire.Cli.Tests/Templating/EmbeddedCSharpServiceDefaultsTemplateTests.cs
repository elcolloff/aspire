// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Templating;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Templating;

/// <summary>
/// Integration tests for <see cref="EmbeddedCSharpServiceDefaultsTemplate"/>.
/// These render the actual embedded template tree into a temp directory and
/// assert on the resulting file layout / content. They are the cross-check
/// that the .NET-template-engine replacement produces output equivalent (in
/// shape) to what <c>dotnet new aspire-servicedefaults</c> previously emitted.
/// </summary>
public class EmbeddedCSharpServiceDefaultsTemplateTests
{
    [Fact]
    public async Task RenderAsync_WritesExpectedFileTree()
    {
        using var output = new TempDir();

        await EmbeddedCSharpServiceDefaultsTemplate.RenderAsync(
            output.Path,
            projectName: "MyApp.ServiceDefaults",
            NullLogger.Instance,
            CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(output.Path, "MyApp.ServiceDefaults.csproj")));
        Assert.True(File.Exists(Path.Combine(output.Path, "Extensions.cs")));
    }

    [Fact]
    public async Task RenderAsync_CsprojSubstitutesTokensAndDefaultsToNet10()
    {
        using var output = new TempDir();

        await EmbeddedCSharpServiceDefaultsTemplate.RenderAsync(
            output.Path,
            projectName: "MyApp.ServiceDefaults",
            NullLogger.Instance,
            CancellationToken.None);

        var csproj = File.ReadAllText(Path.Combine(output.Path, "MyApp.ServiceDefaults.csproj"));

        // No unresolved tokens or upstream REPLACE_WITH markers should remain.
        Assert.DoesNotContain("{{", csproj);
        Assert.DoesNotContain("!!REPLACE_WITH_", csproj);

        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", csproj);

        // Sanity-check that every package reference now has a concrete version stamped in.
        Assert.Contains("Include=\"Microsoft.Extensions.Http.Resilience\" Version=\"", csproj);
        Assert.Contains("Include=\"Microsoft.Extensions.ServiceDiscovery\" Version=\"", csproj);
        Assert.Contains("Include=\"OpenTelemetry.Exporter.OpenTelemetryProtocol\" Version=\"", csproj);
        Assert.Contains("Include=\"OpenTelemetry.Extensions.Hosting\" Version=\"", csproj);
        Assert.Contains("Include=\"OpenTelemetry.Instrumentation.AspNetCore\" Version=\"", csproj);
        Assert.Contains("Include=\"OpenTelemetry.Instrumentation.Http\" Version=\"", csproj);
        Assert.Contains("Include=\"OpenTelemetry.Instrumentation.Runtime\" Version=\"", csproj);
    }

    [Fact]
    public async Task RenderAsync_HonorsExplicitTargetFramework()
    {
        using var output = new TempDir();

        await EmbeddedCSharpServiceDefaultsTemplate.RenderAsync(
            output.Path,
            projectName: "MyApp.ServiceDefaults",
            NullLogger.Instance,
            CancellationToken.None,
            targetFramework: "net9.0");

        var csproj = File.ReadAllText(Path.Combine(output.Path, "MyApp.ServiceDefaults.csproj"));
        Assert.Contains("<TargetFramework>net9.0</TargetFramework>", csproj);
    }

    [Fact]
    public async Task RenderAsync_ExtensionsCsCopiedVerbatim()
    {
        using var output = new TempDir();

        await EmbeddedCSharpServiceDefaultsTemplate.RenderAsync(
            output.Path,
            projectName: "MyApp.ServiceDefaults",
            NullLogger.Instance,
            CancellationToken.None);

        var extensions = File.ReadAllText(Path.Combine(output.Path, "Extensions.cs"));

        // Extensions.cs is pure C# without any templated tokens; assert on a couple of
        // distinctive snippets to confirm the file copy preserved the upstream content.
        Assert.Contains("public static TBuilder AddServiceDefaults<TBuilder>", extensions);
        Assert.Contains("public static WebApplication MapDefaultEndpoints", extensions);
        Assert.DoesNotContain("{{", extensions);
    }

    [Fact]
    public void PackageVersionsAreBakedIntoAssembly()
    {
        // Sanity-check that the AssemblyMetadata wiring exposes non-empty values for
        // every key the embedded template consumes. A regression here surfaces as a
        // confusing csproj-render-time exception otherwise.
        Assert.False(string.IsNullOrWhiteSpace(EmbeddedTemplatePackageVersions.MicrosoftExtensionsHttpResilienceVersion));
        Assert.False(string.IsNullOrWhiteSpace(EmbeddedTemplatePackageVersions.MicrosoftExtensionsServiceDiscoveryVersion));
        Assert.False(string.IsNullOrWhiteSpace(EmbeddedTemplatePackageVersions.OpenTelemetryExporterOpenTelemetryProtocolVersion));
        Assert.False(string.IsNullOrWhiteSpace(EmbeddedTemplatePackageVersions.OpenTelemetryInstrumentationExtensionsHostingVersion));
        Assert.False(string.IsNullOrWhiteSpace(EmbeddedTemplatePackageVersions.OpenTelemetryInstrumentationAspNetCoreVersion));
        Assert.False(string.IsNullOrWhiteSpace(EmbeddedTemplatePackageVersions.OpenTelemetryInstrumentationHttpVersion));
        Assert.False(string.IsNullOrWhiteSpace(EmbeddedTemplatePackageVersions.OpenTelemetryInstrumentationRuntimeVersion));
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Directory = System.IO.Directory.CreateTempSubdirectory("aspire-cli-csharp-servicedefaults-tests-");
        }

        public DirectoryInfo Directory { get; }
        public string Path => Directory.FullName;
        public void Dispose()
        {
            try
            {
                Directory.Delete(recursive: true);
            }
            catch
            {
            }
        }
    }
}
