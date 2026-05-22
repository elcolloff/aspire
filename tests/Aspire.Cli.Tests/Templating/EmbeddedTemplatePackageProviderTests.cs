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
    public async Task EnsureExtractedAsync_ExtractsEmbeddedPackage_ToVersionScopedCacheDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var executionContext = CreateExecutionContext(workspace);
        var provider = new EmbeddedTemplatePackageProvider(executionContext, NullLogger<EmbeddedTemplatePackageProvider>.Instance);

        var extracted = await provider.EnsureExtractedAsync(CancellationToken.None).DefaultTimeout();

        Assert.True(extracted.Exists, $"Expected extracted nupkg at {extracted.FullName}");
        Assert.Equal("Aspire.ProjectTemplates.nupkg", extracted.Name);

        // Path layout: {AspireHomeDirectory}/templates/{cli-version}/Aspire.ProjectTemplates.nupkg
        var expectedVersionDir = VersionHelper.GetDefaultTemplateVersion().Replace('+', '_');
        var parentDir = extracted.Directory!;
        Assert.Equal(expectedVersionDir, parentDir.Name);
        Assert.Equal("templates", parentDir.Parent!.Name);
        Assert.Equal(executionContext.AspireHomeDirectory.FullName, parentDir.Parent!.Parent!.FullName);

        // Nupkg payloads start with the ZIP magic bytes "PK\x03\x04".
        var bytes = await File.ReadAllBytesAsync(extracted.FullName).DefaultTimeout();
        Assert.True(bytes.Length > 0);
        Assert.Equal(0x50, bytes[0]);
        Assert.Equal(0x4B, bytes[1]);
        Assert.Equal(0x03, bytes[2]);
        Assert.Equal(0x04, bytes[3]);
    }

    [Fact]
    public async Task EnsureExtractedAsync_SecondCall_ReturnsExistingFileWithoutRewriting()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var executionContext = CreateExecutionContext(workspace);
        var provider = new EmbeddedTemplatePackageProvider(executionContext, NullLogger<EmbeddedTemplatePackageProvider>.Instance);

        var first = await provider.EnsureExtractedAsync(CancellationToken.None).DefaultTimeout();
        var firstWriteTime = File.GetLastWriteTimeUtc(first.FullName);

        // Sleep just enough that a rewrite would produce a distinguishable timestamp on
        // file systems with low-resolution mtime (HFS+ / FAT). 1.1s covers the worst case.
        await Task.Delay(TimeSpan.FromMilliseconds(1100), TestContext.Current.CancellationToken);

        var second = await provider.EnsureExtractedAsync(CancellationToken.None).DefaultTimeout();
        var secondWriteTime = File.GetLastWriteTimeUtc(second.FullName);

        Assert.Equal(first.FullName, second.FullName);
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

        var distinctPaths = results.Select(r => r.FullName).Distinct().ToArray();
        Assert.Single(distinctPaths);
        Assert.All(results, r => Assert.True(r.Exists));

        // Verify no .tmp-* leftovers were stranded by losing racers.
        var leftovers = results[0].Directory!.EnumerateFiles("*.tmp-*").ToArray();
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
