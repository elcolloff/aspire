// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Interaction;
using Aspire.Cli.NuGet;
using Aspire.Shared;
using Microsoft.Extensions.Logging;
using Semver;

namespace Aspire.Cli.Utils;

internal interface ICliUpdateNotifier
{
    Task CheckForCliUpdatesAsync(DirectoryInfo workingDirectory, CancellationToken cancellationToken);
    void NotifyIfUpdateAvailable();
    Task NotifyIfUpdateAvailableAsync(DirectoryInfo workingDirectory, TimeSpan waitTimeout, CancellationToken cancellationToken);
    bool IsUpdateAvailable();
}

internal class CliUpdateNotifier(
    ILogger<CliUpdateNotifier> logger,
    INuGetPackageCache nuGetPackageCache,
    IInteractionService interactionService) : ICliUpdateNotifier
{
    private readonly object _updateCheckLock = new();
    private IEnumerable<Shared.NuGetPackageCli>? _availablePackages;
    private Task<IEnumerable<Shared.NuGetPackageCli>>? _updateCheckTask;

    public async Task CheckForCliUpdatesAsync(DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        var updateCheckTask = GetOrStartUpdateCheckTask(workingDirectory, cancellationToken);
        _availablePackages = await updateCheckTask;
    }

    public void NotifyIfUpdateAvailable()
    {
        if (_availablePackages is null)
        {
            return;
        }

        var currentVersion = GetCurrentVersion();
        if (currentVersion is null)
        {
            logger.LogDebug("Unable to determine current CLI version for update check.");
            return;
        }

        var newerVersion = PackageUpdateHelpers.GetNewerVersion(logger, currentVersion, _availablePackages);

        if (newerVersion is not null)
        {
            var updateCommand = DotNetToolDetection.GetDotNetToolUpdateCommand() ?? "aspire update";

            interactionService.DisplayVersionUpdateNotification(newerVersion.ToString(), updateCommand);
        }
    }

    public async Task NotifyIfUpdateAvailableAsync(DirectoryInfo workingDirectory, TimeSpan waitTimeout, CancellationToken cancellationToken)
    {
        var updateCheckTask = GetOrStartUpdateCheckTask(workingDirectory, cancellationToken);

        if (updateCheckTask is not null)
        {
            using var timeoutCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellationTokenSource.CancelAfter(waitTimeout);

            try
            {
                var availablePackages = await updateCheckTask.WaitAsync(timeoutCancellationTokenSource.Token);
                _availablePackages = availablePackages;
            }
            catch (OperationCanceledException) when (timeoutCancellationTokenSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                return;
            }
        }

        NotifyIfUpdateAvailable();
    }

    public bool IsUpdateAvailable()
    {
        if (_availablePackages is null)
        {
            return false;
        }

        var currentVersion = GetCurrentVersion();
        if (currentVersion is null)
        {
            return false;
        }

        var newerVersion = PackageUpdateHelpers.GetNewerVersion(logger, currentVersion, _availablePackages);
        return newerVersion is not null;
    }

    protected virtual SemVersion? GetCurrentVersion()
    {
        return PackageUpdateHelpers.GetCurrentPackageVersion();
    }

    private Task<IEnumerable<Shared.NuGetPackageCli>> GetOrStartUpdateCheckTask(DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        lock (_updateCheckLock)
        {
            if (_updateCheckTask is null || _updateCheckTask.IsCanceled || _updateCheckTask.IsFaulted)
            {
                _updateCheckTask = nuGetPackageCache.GetCliPackagesAsync(
                    workingDirectory: workingDirectory,
                    prerelease: true,
                    nugetConfigFile: null,
                    cancellationToken: cancellationToken);
            }

            return _updateCheckTask;
        }
    }
}
