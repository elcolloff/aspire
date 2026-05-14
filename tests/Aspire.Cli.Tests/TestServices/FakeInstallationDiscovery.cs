// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// Fixed-result <see cref="IInstallationDiscovery"/> for InfoCommand tests.
/// Returns a curated <c>self</c> row and (optionally) curated peers from
/// <see cref="DiscoverAllAsync"/>, decoupling the test from host filesystem
/// state, <c>$PATH</c>, and any real sidecar files.
/// </summary>
internal sealed class FakeInstallationDiscovery : IInstallationDiscovery
{
    private readonly InstallationInfo _self;
    private readonly IReadOnlyList<InstallationInfo> _others;

    public FakeInstallationDiscovery(InstallationInfo self, IReadOnlyList<InstallationInfo>? others = null)
    {
        _self = self;
        _others = others ?? [];
    }

    public InstallationInfo DescribeSelf() => _self;

    public Task<IReadOnlyList<InstallationInfo>> DiscoverAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Self is always the first element by InstallationDiscovery contract.
        IReadOnlyList<InstallationInfo> all = [_self, .. _others];
        return Task.FromResult(all);
    }
}
