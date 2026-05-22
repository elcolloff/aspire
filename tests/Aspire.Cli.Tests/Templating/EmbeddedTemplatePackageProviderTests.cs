// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Templating;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Templating;

public class EmbeddedTemplatePackageProviderTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task EnsureExtractedAsync_ExtractsEmbeddedArchive_ToVersionScopedCacheDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var executionContext = CreateExecutionContext(workspace);
        var provider = new EmbeddedTemplatePackageProvider(executionContext, NullLogger<EmbeddedTemplatePackageProvider>.Instance);

        var extracted = await provider.EnsureExtractedAsync(CancellationToken.None).DefaultTimeout();

        Assert.True(extracted.TemplatesDirectory.Exists, $"Expected templates dir at {extracted.TemplatesDirectory.FullName}");
        Assert.True(extracted.DotnetCliHomeDirectory.Exists, $"Expected cli-home dir at {extracted.DotnetCliHomeDirectory.FullName}");

        // Layout: {AspireHomeDirectory}/templates/{cli-version}/{templates|cli-home}/...
        var expectedVersionDir = VersionHelper.GetDefaultTemplateVersion().Replace('+', '_');
        Assert.Equal("templates", extracted.TemplatesDirectory.Name);
        Assert.Equal("cli-home", extracted.DotnetCliHomeDirectory.Name);
        var versionDir = extracted.TemplatesDirectory.Parent!;
        Assert.Equal(expectedVersionDir, versionDir.Name);
        Assert.Equal("templates", versionDir.Parent!.Name);
        Assert.Equal(executionContext.AspireHomeDirectory.FullName, versionDir.Parent!.Parent!.FullName);

        // At least one extracted template should contain a .template.config folder, which
        // is what `dotnet new install <dir>` keys on.
        var templateConfigs = extracted.TemplatesDirectory.EnumerateDirectories(".template.config", SearchOption.AllDirectories).ToArray();
        Assert.NotEmpty(templateConfigs);
    }

    [Fact]
    public async Task EnsureExtractedAsync_SecondCall_ReturnsSamePathsWithoutRewriting()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var executionContext = CreateExecutionContext(workspace);
        var provider = new EmbeddedTemplatePackageProvider(executionContext, NullLogger<EmbeddedTemplatePackageProvider>.Instance);

        var first = await provider.EnsureExtractedAsync(CancellationToken.None).DefaultTimeout();
        var sentinelPath = Path.Combine(first.TemplatesDirectory.FullName, ".aspire-extracted");
        Assert.True(File.Exists(sentinelPath), $"Sentinel missing at {sentinelPath}");
        var firstWriteTime = File.GetLastWriteTimeUtc(sentinelPath);

        // Sleep just enough that a rewrite would produce a distinguishable timestamp on
        // file systems with low-resolution mtime (HFS+ / FAT). 1.1s covers the worst case.
        await Task.Delay(TimeSpan.FromMilliseconds(1100), TestContext.Current.CancellationToken);

        var second = await provider.EnsureExtractedAsync(CancellationToken.None).DefaultTimeout();
        var secondWriteTime = File.GetLastWriteTimeUtc(sentinelPath);

        Assert.Equal(first.TemplatesDirectory.FullName, second.TemplatesDirectory.FullName);
        Assert.Equal(first.DotnetCliHomeDirectory.FullName, second.DotnetCliHomeDirectory.FullName);
        Assert.Equal(firstWriteTime, secondWriteTime);
    }

    [Fact]
    public async Task EnsureExtractedAsync_ConcurrentCallers_AllSucceedAndReturnSamePath()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var executionContext = CreateExecutionContext(workspace);
        var provider = new EmbeddedTemplatePackageProvider(executionContext, NullLogger<EmbeddedTemplatePackageProvider>.Instance);

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => provider.EnsureExtractedAsync(CancellationToken.None)))
            .ToArray();

        var results = await Task.WhenAll(tasks).DefaultTimeout();

        var distinctPaths = results.Select(r => r.TemplatesDirectory.FullName).Distinct().ToArray();
        Assert.Single(distinctPaths);
        Assert.All(results, r => Assert.True(r.TemplatesDirectory.Exists));

        // Verify no .tmp-* leftovers were stranded by losing racers.
        var versionDir = results[0].TemplatesDirectory.Parent!;
        var leftovers = versionDir.EnumerateDirectories("*.tmp-*").ToArray();
        Assert.Empty(leftovers);
    }

    private static CliExecutionContext CreateExecutionContext(TemporaryWorkspace workspace)
    {
        var root = workspace.WorkspaceRoot;
        var hivesDir = new DirectoryInfo(Path.Combine(root.FullName, ".aspire", "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(root.FullName, ".aspire", "cache"));
        var sdksDir = new DirectoryInfo(Path.Combine(root.FullName, ".aspire", "sdks"));
        var logsDir = new DirectoryInfo(Path.Combine(root.FullName, ".aspire", "logs"));
        var aspireHomeDir = new DirectoryInfo(Path.Combine(root.FullName, ".aspire"));

        return new CliExecutionContext(
            workingDirectory: root,
            hivesDirectory: hivesDir,
            cacheDirectory: cacheDir,
            sdksDirectory: sdksDir,
            logsDirectory: logsDir,
            logFilePath: Path.Combine(logsDir.FullName, "test.log"),
            aspireHomeDirectory: aspireHomeDir);
    }
}
