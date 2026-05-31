// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Packaging;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestPackagingService : IPackagingService
{
    public Func<CancellationToken, Task<IEnumerable<PackageChannel>>>? GetChannelsAsyncCallback { get; set; }
    public string? LastRequestedChannelName { get; private set; }

    /// <summary>
    /// Optional callback to control the reason returned by
    /// <see cref="GetStagingChannelUnavailableReason"/>. When <see langword="null"/> (the default),
    /// the fake reports staging as available (returns <see langword="null"/>) so existing tests
    /// that don't care about staging gating keep working unchanged.
    /// </summary>
    public Func<string?>? GetStagingChannelUnavailableReasonCallback { get; set; }

    public Task<IEnumerable<PackageChannel>> GetChannelsAsync(CancellationToken cancellationToken = default, string? requestedChannelName = null)
    {
        LastRequestedChannelName = requestedChannelName;

        if (GetChannelsAsyncCallback is not null)
        {
            return GetChannelsAsyncCallback(cancellationToken);
        }

        // Default: Return a fake channel with template packages
        var testChannel = PackageChannel.CreateImplicitChannel(new FakeNuGetPackageCache(), new TestFeatures());
        return Task.FromResult<IEnumerable<PackageChannel>>(new[] { testChannel });
    }

    public string? GetStagingChannelUnavailableReason()
    {
        return GetStagingChannelUnavailableReasonCallback?.Invoke();
    }

    /// <summary>
    /// Optional callback for <see cref="GetEffectiveIdentityChannel"/>. Returning
    /// <see langword="null"/> or an empty string (the default) signals "no opinion" so
    /// <c>NewCommand</c> falls back to <see cref="CliExecutionContext.IdentityChannel"/>
    /// — the value tests set via the channel-resolution helpers in this project.
    /// </summary>
    public Func<string?>? GetEffectiveIdentityChannelCallback { get; set; }

    public string GetEffectiveIdentityChannel()
        => GetEffectiveIdentityChannelCallback?.Invoke() ?? string.Empty;
}
