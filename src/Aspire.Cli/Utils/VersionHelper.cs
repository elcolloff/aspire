// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Cli.Packaging;
using Aspire.Cli.Resources;
using Aspire.Shared;

namespace Aspire.Cli.Utils;

internal static class VersionHelper
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="channelName"/> identifies a
    /// locally-built channel — a PR hive (<c>pr-*</c>), a workflow-run hive (<c>run-*</c>),
    /// or a local development build (<c>local</c>).
    /// </summary>
    public static bool IsLocalBuildChannel(string? channelName)
    {
        return channelName is not null &&
            (channelName.Equals(PackageChannelNames.Local, StringComparison.OrdinalIgnoreCase) ||
             channelName.StartsWith("pr-", StringComparison.OrdinalIgnoreCase) ||
             channelName.StartsWith("run-", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Finds the candidate that exactly matches the current CLI/SDK version when a channel has already been selected or local hives are present.
    /// </summary>
    public static bool TryGetCurrentCliVersionMatch<T>(
        IEnumerable<T> candidates,
        Func<T, string?> versionSelector,
        [MaybeNullWhen(false)] out T match,
        string? channelName,
        bool hasPrHives,
        CliExecutionContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(versionSelector);

        if (!hasPrHives && string.IsNullOrWhiteSpace(channelName))
        {
            match = default;
            return false;
        }

        var cliVersion = GetDefaultSdkVersion(context);
        foreach (var candidate in candidates)
        {
            if (string.Equals(versionSelector(candidate), cliVersion, StringComparison.OrdinalIgnoreCase))
            {
                match = candidate;
                return true;
            }
        }

        match = default;
        return false;
    }

    public static string GetDefaultTemplateVersion(CliExecutionContext? context = null)
    {
        // Honor the same diagnostic override that PackagingService uses to simulate a
        // different CLI identity/version for end-to-end staging validation. Without this,
        // a locally built CLI emulating a staging build via `eng/scripts/debug-staging.sh`
        // would correctly ROUTE package search at the darc staging feed (PackagingService
        // reads the override) but would still REPORT its own stamped assembly version to
        // everything else — template selection (TryGetCurrentCliVersionMatch), the SDK
        // version written into aspire.config.json, the CLI banner, telemetry, etc. The
        // resulting half-emulation surfaces a "CLI version differs from configured SDK
        // version" warning and stamps the wrong SDK version into newly-scaffolded
        // projects.
        //
        // Read the env var through CliExecutionContext when a context is supplied so the
        // process-environment vs. test-supplied-dictionary distinction matches the rest of
        // the CLI (see CliExecutionContext.GetEnvironmentVariable's "if a custom env was
        // provided, do not fall back to the process" contract). When no context is
        // supplied (call sites that don't have one handy, like the banner / telemetry),
        // fall back to the process env directly — those callers never participated in the
        // override loop before this PR, so process-env reads preserve their behavior.
        // See https://github.com/microsoft/aspire/blob/main/docs/cli-staging-validation.md
        // and PackagingService.OverrideCliInformationalVersionConfigKey.
        var overrideVersion = context is not null
            ? context.GetEnvironmentVariable("overrideCliInformationalVersion")
            : Environment.GetEnvironmentVariable("overrideCliInformationalVersion");
        if (!string.IsNullOrWhiteSpace(overrideVersion))
        {
            return overrideVersion;
        }

        return PackageUpdateHelpers.GetCurrentAssemblyVersion() ?? throw new InvalidOperationException(ErrorStrings.UnableToRetrieveAssemblyVersion);
    }

    /// <summary>
    /// Gets the default Aspire SDK version based on the CLI version.
    /// The CLI version is the SDK version — the bundled server and packages must match.
    /// </summary>
    public static string GetDefaultSdkVersion(CliExecutionContext? context = null)
    {
        var version = GetDefaultTemplateVersion(context);

        // Strip the commit SHA suffix (e.g., "9.2.0+abc123" -> "9.2.0")
        var plusIndex = version.IndexOf('+');
        if (plusIndex > 0)
        {
            version = version[..plusIndex];
        }

        return version;
    }
}
