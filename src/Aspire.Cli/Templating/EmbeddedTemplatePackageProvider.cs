// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Templating;

/// <summary>
/// Materializes the Aspire.ProjectTemplates archive that is embedded in the CLI assembly
/// (see <c>Aspire.Cli.csproj</c> EmbeddedResource entry) to a stable on-disk directory
/// that can be handed to <c>dotnet new install</c>, alongside a private
/// <c>DOTNET_CLI_HOME</c> so the template install is fully isolated from the user's
/// global template index.
/// </summary>
/// <remarks>
/// <para>
/// Embedding the templates archive inside the CLI binary eliminates all NuGet-based
/// resolution of templates. The archive shipped inside any given CLI build is, by
/// construction, the templates produced by the same build — so the version, SHA, and
/// channel of the templates can never drift from the CLI. Every acquisition scenario
/// (workspace build, localhive, PR build, daily, staging, stable) shares a single
/// deterministic path.
/// </para>
/// <para>
/// Extraction is lazy and idempotent. The archive is extracted to
/// <c>{AspireHomeDirectory}/templates/{cli-version}/templates/</c> the first time it is
/// requested and reused on every subsequent invocation of the same CLI build. The
/// sibling <c>{AspireHomeDirectory}/templates/{cli-version}/cli-home/</c> directory is
/// used as a private <c>DOTNET_CLI_HOME</c> so all template-engine state (installed
/// packages, template indexes, settings) lives under Aspire's own home and never
/// touches <c>~/.templateengine</c> or the user's other dotnet caches.
/// </para>
/// <para>
/// Concurrent extractions from multiple processes are safe: the extraction happens into
/// a sibling temp directory which is then atomically renamed into place; equally-versioned
/// binaries write byte-identical payloads, so the rename winner does not matter.
/// </para>
/// </remarks>
internal sealed class EmbeddedTemplatePackageProvider(
    CliExecutionContext executionContext,
    ILogger<EmbeddedTemplatePackageProvider> logger)
{
    // Must match the LogicalName in Aspire.Cli.csproj (_ResolveEmbeddedTemplatesNupkg target).
    private const string EmbeddedResourceName = "Aspire.ProjectTemplates.zip";

    // Sentinel file that signals a fully-extracted templates directory. Created last so
    // partial extractions are recoverable by ignoring directories without the sentinel.
    private const string ExtractionCompleteMarker = ".aspire-extracted";

    /// <summary>
    /// Returns the on-disk paths of the extracted templates directory and the private
    /// <c>DOTNET_CLI_HOME</c> to use for template-engine operations, extracting the
    /// embedded archive on first call.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the templates archive is not embedded in the CLI assembly. This only
    /// happens for builds produced with <c>SkipEmbeddedTemplatesNupkg=true</c>.
    /// </exception>
    public async Task<EmbeddedTemplatesLocation> EnsureExtractedAsync(CancellationToken cancellationToken)
    {
        var cliVersion = VersionHelper.GetDefaultTemplateVersion();

        // '+' is legal in path components on all supported OSes but tooling occasionally
        // treats it as a separator in display strings. Normalize to '_' for the cache
        // directory so the on-disk path is uniform regardless of build SHA suffix shape.
        var versionDirName = cliVersion.Replace('+', '_');

        var rootDir = new DirectoryInfo(Path.Combine(
            executionContext.AspireHomeDirectory.FullName,
            "templates",
            versionDirName));

        var templatesDir = new DirectoryInfo(Path.Combine(rootDir.FullName, "templates"));
        var cliHomeDir = new DirectoryInfo(Path.Combine(rootDir.FullName, "cli-home"));

        var sentinelPath = Path.Combine(templatesDir.FullName, ExtractionCompleteMarker);
        if (File.Exists(sentinelPath))
        {
            logger.LogDebug("Embedded templates already extracted at {TemplatesDir}; reusing.", templatesDir.FullName);
            // Ensure the private CLI home directory exists; it is created lazily here in
            // case a previous CLI version extracted templates but the cli-home dir was
            // pruned externally.
            cliHomeDir.Create();
            return new EmbeddedTemplatesLocation(templatesDir, cliHomeDir);
        }

        logger.LogDebug("Extracting embedded templates for CLI version {CliVersion} to {TemplatesDir}.", cliVersion, templatesDir.FullName);
        rootDir.Create();
        cliHomeDir.Create();

        var assembly = typeof(EmbeddedTemplatePackageProvider).Assembly;
        await using var resourceStream = assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{EmbeddedResourceName}' was not found in the CLI assembly. " +
                $"This build was produced with SkipEmbeddedTemplatesNupkg=true; rebuild without that property.");

        // Extract into a sibling temp directory and rename so concurrent CLI processes
        // never see a partially-populated templates tree. Equally-versioned binaries write
        // byte-identical payloads so the rename winner does not matter.
        var tempDirPath = templatesDir.FullName + ".tmp-" + Path.GetRandomFileName();
        try
        {
            Directory.CreateDirectory(tempDirPath);
            using (var zip = new ZipArchive(resourceStream, ZipArchiveMode.Read, leaveOpen: false))
            {
                zip.ExtractToDirectory(tempDirPath, overwriteFiles: true);
            }

            // Write the sentinel last so a crash mid-extract leaves the directory
            // unmarked and we retry on the next run.
            await File.WriteAllTextAsync(Path.Combine(tempDirPath, ExtractionCompleteMarker), cliVersion, cancellationToken).ConfigureAwait(false);

            try
            {
                Directory.Move(tempDirPath, templatesDir.FullName);
                logger.LogDebug("Extracted embedded templates to {TemplatesDir}.", templatesDir.FullName);
            }
            catch (IOException) when (File.Exists(sentinelPath))
            {
                // Lost the rename race against another CLI process that wrote the
                // byte-identical payload — that's a success path. Drop our copy and use
                // the existing directory.
                logger.LogDebug("Another process populated {TemplatesDir} concurrently; using the existing tree and discarding {TempDir}.", templatesDir.FullName, tempDirPath);
                TryDeleteDirectory(tempDirPath);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to extract embedded templates to {TemplatesDir}.", templatesDir.FullName);
            TryDeleteDirectory(tempDirPath);
            throw;
        }

        return new EmbeddedTemplatesLocation(templatesDir, cliHomeDir);
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            // Best-effort cleanup; a leftover .tmp-* directory is harmless.
            logger.LogDebug(ex, "Failed to delete temp directory {Path}; ignoring.", path);
        }
    }
}

/// <summary>
/// On-disk locations produced by <see cref="EmbeddedTemplatePackageProvider"/>.
/// </summary>
/// <param name="TemplatesDirectory">Directory containing the extracted template tree;
/// suitable to pass to <c>dotnet new install</c>.</param>
/// <param name="DotnetCliHomeDirectory">Private <c>DOTNET_CLI_HOME</c> to set on
/// <c>dotnet new install</c> and subsequent <c>dotnet new &lt;template&gt;</c>
/// invocations so the template engine's per-user state is fully isolated from the
/// global user profile.</param>
internal readonly record struct EmbeddedTemplatesLocation(DirectoryInfo TemplatesDirectory, DirectoryInfo DotnetCliHomeDirectory);
