// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Acquisition;

/// <summary>
/// Default <see cref="IInstallationDiscovery"/>. The self-describe path
/// composes data already available in-process (channel from
/// <see cref="IIdentityChannelReader"/>, version from
/// <see cref="VersionHelper.GetDefaultTemplateVersion"/>, route from the
/// running binary's sidecar) so it is cheap and side-effect-free.
/// </summary>
/// <remarks>
/// The <c>--all</c> path is intentionally implemented as a thin wrapper
/// around self-describe in this initial cut: a follow-up pass adds
/// filesystem walking, the trust gate, and the peer probe. Until then,
/// <see cref="DiscoverAllAsync"/> returns a single-element list — the
/// schema is array-typed so the v1 JSON shape stays stable when the full
/// discovery lands.
/// </remarks>
internal sealed class InstallationDiscovery : IInstallationDiscovery
{
    private readonly IIdentityChannelReader _channelReader;
    private readonly IInstallSidecarReader _sidecarReader;
    private readonly ILogger<InstallationDiscovery> _logger;

    public InstallationDiscovery(
        IIdentityChannelReader channelReader,
        IInstallSidecarReader sidecarReader,
        ILogger<InstallationDiscovery> logger)
    {
        ArgumentNullException.ThrowIfNull(channelReader);
        ArgumentNullException.ThrowIfNull(sidecarReader);
        ArgumentNullException.ThrowIfNull(logger);

        _channelReader = channelReader;
        _sidecarReader = sidecarReader;
        _logger = logger;
    }

    /// <inheritdoc />
    public InstallationInfo DescribeSelf()
    {
        var processPath = Environment.ProcessPath;
        var canonicalPath = ResolveCanonicalPath(processPath);
        var binaryDir = !string.IsNullOrEmpty(canonicalPath) ? Path.GetDirectoryName(canonicalPath) : null;

        var sidecar = !string.IsNullOrEmpty(binaryDir) ? _sidecarReader.TryRead(binaryDir) : null;
        // Use the wire string from the parsed source so callers see the same
        // identifier the install scripts wrote, not the C# enum name. For
        // sidecars with an unrecognized source value we surface the raw
        // string so users see "(unknown: future-route)" rather than nothing.
        var route = sidecar?.Source.ToWireString() ?? sidecar?.RawSource;

        return new InstallationInfo
        {
            Path = processPath ?? string.Empty,
            CanonicalPath = canonicalPath,
            Version = VersionHelper.GetDefaultTemplateVersion(),
            Channel = TryReadChannel(),
            Route = route,
            IsOnPath = IsOnPathSelf(canonicalPath),
            Status = InstallationInfoStatus.Ok,
        };
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<InstallationInfo>> DiscoverAllAsync(CancellationToken cancellationToken)
    {
        // v1: returns only the running CLI. Filesystem walk + peer probe
        // lands in a follow-up commit; the public surface (array shape) is
        // already correct, so the contract does not change when that ships.
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<InstallationInfo> single = [DescribeSelf()];
        return Task.FromResult(single);
    }

    /// <summary>
    /// Resolves any symlinks in <paramref name="processPath"/> so that two
    /// PATH entries pointing at the same backing file produce the same
    /// canonical identifier. Mirrors the symlink resolution that
    /// <see cref="Bundles.BundleService"/> uses for sidecar lookup so
    /// <c>info</c> and <c>BundleService</c> agree on identity.
    /// </summary>
    private static string? ResolveCanonicalPath(string? processPath)
    {
        if (string.IsNullOrEmpty(processPath))
        {
            return null;
        }

        try
        {
            var resolved = File.ResolveLinkTarget(processPath, returnFinalTarget: true);
            return resolved?.FullName ?? Path.GetFullPath(processPath);
        }
        catch (IOException)
        {
            return Path.GetFullPath(processPath);
        }
    }

    private string? TryReadChannel()
    {
        try
        {
            return _channelReader.ReadChannel();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same defensive posture as doctor: a misconfigured dev build
            // with no AspireCliChannel assembly metadata must not break
            // aspire info.
            _logger.LogDebug(ex, "Could not read identity channel for InstallationDiscovery.");
            return null;
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when the canonical resolution of
    /// <c>aspire</c> on the current <c>$PATH</c> matches <paramref name="canonicalSelfPath"/>.
    /// We resolve via the same canonicalization function so symlinked
    /// install layouts (e.g. homebrew's <c>bin/aspire -&gt; staged_path/aspire</c>)
    /// don't false-negative.
    /// </summary>
    private static bool IsOnPathSelf(string? canonicalSelfPath)
    {
        if (string.IsNullOrEmpty(canonicalSelfPath))
        {
            return false;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var binaryNames = OperatingSystem.IsWindows() ? new[] { "aspire.exe", "aspire" } : new[] { "aspire" };
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in binaryNames)
            {
                var candidate = Path.Combine(dir, name);
                if (!File.Exists(candidate))
                {
                    continue;
                }

                var canonicalCandidate = ResolveCanonicalPath(candidate);
                if (!string.IsNullOrEmpty(canonicalCandidate) && comparer.Equals(canonicalCandidate, canonicalSelfPath))
                {
                    return true;
                }

                // First hit on $PATH wins regardless of whether it matched —
                // shell behavior is to use the leftmost match, so anything
                // later cannot represent "the aspire on $PATH".
                return false;
            }
        }

        return false;
    }
}
