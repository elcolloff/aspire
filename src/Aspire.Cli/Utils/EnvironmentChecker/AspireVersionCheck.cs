// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json.Nodes;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Utils.EnvironmentChecker;

/// <summary>
/// Reports the installed Aspire CLI version, whether a newer CLI version is available, and
/// the Aspire SDK version used by the selected AppHost when one can be discovered.
/// </summary>
internal sealed class AspireVersionCheck(
    ICliUpdateNotifier updateNotifier,
    IProjectLocator projectLocator,
    ILanguageDiscovery languageDiscovery,
    IAppHostProjectFactory projectFactory,
    CliExecutionContext executionContext,
    ILogger<AspireVersionCheck> logger) : IEnvironmentCheck
{
    // Version checks should appear first so users immediately see which Aspire bits
    // produced the rest of the doctor output before reading environment diagnostics.
    public int Order => 0;

    public async Task<IReadOnlyList<EnvironmentCheckResult>> CheckAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<EnvironmentCheckResult>
        {
            await GetCliVersionCheckAsync(cancellationToken)
        };

        var appHostVersionCheck = await GetAppHostVersionCheckAsync(cancellationToken);
        if (appHostVersionCheck is not null)
        {
            results.Add(appHostVersionCheck);
        }

        return results;
    }

    private async Task<EnvironmentCheckResult> GetCliVersionCheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            var status = await updateNotifier.GetVersionStatusAsync(executionContext.WorkingDirectory, cancellationToken);
            var currentVersion = string.IsNullOrWhiteSpace(status.CurrentVersion) ? DoctorCommandStrings.VersionUnknown : status.CurrentVersion;

            // Doctor should always report the installed CLI version. Treat update lookup
            // failures as a warning on that same check rather than hiding the version or
            // failing the command because offline/private-feed scenarios are common.
            if (status.UpdateCheckError is { Length: > 0 } updateCheckError)
            {
                return new EnvironmentCheckResult
                {
                    Category = "aspire",
                    Name = "cli-version",
                    Status = EnvironmentCheckStatus.Warning,
                    Message = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.CliVersionMessageFormat, currentVersion),
                    Details = $"{DoctorCommandStrings.CliVersionUpdateCheckFailedMessage}: {updateCheckError}",
                    Metadata = BuildCliVersionMetadata(currentVersion, latestVersion: null, status.UpdateCommand, updateCheckError)
                };
            }

            if (status.LatestVersion is { Length: > 0 } latestVersion)
            {
                return new EnvironmentCheckResult
                {
                    Category = "aspire",
                    Name = "cli-version",
                    Status = EnvironmentCheckStatus.Warning,
                    Message = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.CliVersionOutOfDateMessageFormat, currentVersion, latestVersion),
                    Fix = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.CliVersionOutOfDateFixFormat, status.UpdateCommand ?? "aspire update"),
                    Metadata = BuildCliVersionMetadata(currentVersion, latestVersion, status.UpdateCommand, updateCheckError: null)
                };
            }

            return new EnvironmentCheckResult
            {
                Category = "aspire",
                Name = "cli-version",
                Status = EnvironmentCheckStatus.Pass,
                Message = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.CliVersionMessageFormat, currentVersion),
                Metadata = BuildCliVersionMetadata(currentVersion, latestVersion: null, status.UpdateCommand, updateCheckError: null)
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to check Aspire CLI version.");

            return new EnvironmentCheckResult
            {
                Category = "aspire",
                Name = "cli-version",
                Status = EnvironmentCheckStatus.Warning,
                Message = DoctorCommandStrings.CliVersionUpdateCheckFailedMessage,
                Details = ex.Message
            };
        }
    }

    private async Task<EnvironmentCheckResult?> GetAppHostVersionCheckAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<FileInfo> appHostFiles;
        try
        {
            appHostFiles = await ResolveAppHostFilesAsync(cancellationToken);
        }
        catch (ProjectLocatorException ex) when (ex.FailureReason is ProjectLocatorFailureReason.NoProjectFileFound or ProjectLocatorFailureReason.ProjectFileDoesntExist)
        {
            // Doctor is useful outside an Aspire app too; no AppHost simply means there is
            // no AppHost version check to include.
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProjectLocatorException ex)
        {
            return new EnvironmentCheckResult
            {
                Category = "apphost",
                Name = "apphost-version",
                Status = EnvironmentCheckStatus.Warning,
                Message = DoctorCommandStrings.AppHostVersionCheckFailedMessage,
                Details = ex.Message
            };
        }

        foreach (var appHostFile in appHostFiles)
        {
            var relativePath = GetRelativePath(appHostFile);
            try
            {
                var (isAppHost, version) = await ResolveAppHostVersionAsync(appHostFile, cancellationToken);
                if (!isAppHost)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(version))
                {
                    return new EnvironmentCheckResult
                    {
                        Category = "apphost",
                        Name = "apphost-version",
                        Status = EnvironmentCheckStatus.Warning,
                        Message = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.AppHostVersionUnknownMessageFormat, relativePath),
                        Metadata = BuildAppHostVersionMetadata(relativePath, version: null)
                    };
                }

                return new EnvironmentCheckResult
                {
                    Category = "apphost",
                    Name = "apphost-version",
                    Status = EnvironmentCheckStatus.Pass,
                    Message = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.AppHostVersionMessageFormat, version, relativePath),
                    Metadata = BuildAppHostVersionMetadata(relativePath, version)
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to check Aspire AppHost version for {AppHostPath}.", appHostFile.FullName);

                return new EnvironmentCheckResult
                {
                    Category = "apphost",
                    Name = "apphost-version",
                    Status = EnvironmentCheckStatus.Warning,
                    Message = DoctorCommandStrings.AppHostVersionCheckFailedMessage,
                    Details = ex.Message,
                    Metadata = BuildAppHostVersionMetadata(relativePath, version: null)
                };
            }
        }

        return null;
    }

    private async Task<IReadOnlyList<FileInfo>> ResolveAppHostFilesAsync(CancellationToken cancellationToken)
    {
        // Match the other doctor checks: honor a configured AppHost first without
        // scanning, then use bounded language detection. Avoid the full project locator
        // walk here because the AppHost version is informational and doctor should stay
        // fast in large repositories that do not contain an AppHost.
        var configuredAppHost = await projectLocator.GetAppHostFromSettingsAsync(cancellationToken);
        if (configuredAppHost is not null)
        {
            return [configuredAppHost];
        }

        // Detect only which AppHost language appears to be present within the bounded
        // search depth, then validate matching candidates until an AppHost is found.
        var detectedLanguageId = await languageDiscovery.DetectLanguageRecursiveAsync(executionContext.WorkingDirectory, cancellationToken);
        if (detectedLanguageId is null)
        {
            return [];
        }

        var detectedLanguage = languageDiscovery.GetLanguageById(detectedLanguageId.Value);
        return detectedLanguage is not null
            ? FindAppHostCandidates(detectedLanguage, cancellationToken).ToArray()
            : [];
    }

    private IEnumerable<FileInfo> FindAppHostCandidates(LanguageInfo language, CancellationToken cancellationToken)
    {
        var root = executionContext.WorkingDirectory.FullName;
        if (!Directory.Exists(root))
        {
            yield break;
        }

        var dirs = new Stack<(string Path, int Depth)>();
        dirs.Push((root, 0));

        while (dirs.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (dir, depth) = dirs.Pop();

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir).ToArray();
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Skipping directory {Directory} while finding AppHost candidates.", dir);
                continue;
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (language.MatchesFile(Path.GetFileName(file)))
                {
                    yield return new FileInfo(file);
                }
            }

            if (depth >= LanguageInfo.DetectionRecurseLimit)
            {
                continue;
            }

            IEnumerable<string> subdirectories;
            try
            {
                subdirectories = Directory.EnumerateDirectories(dir).ToArray();
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Skipping subdirectories of {Directory} while finding AppHost candidates.", dir);
                continue;
            }

            foreach (var subdirectory in subdirectories)
            {
                dirs.Push((subdirectory, depth + 1));
            }
        }
    }

    private async Task<(bool IsAppHost, string? Version)> ResolveAppHostVersionAsync(FileInfo appHostFile, CancellationToken cancellationToken)
    {
        var project = projectFactory.TryGetProject(appHostFile);
        if (project is null)
        {
            return (false, null);
        }

        var validationResult = await project.ValidateAppHostAsync(appHostFile, cancellationToken);
        if (validationResult.IsValid)
        {
            return (true, validationResult.AspireHostingVersion ?? await project.GetAspireHostingVersionAsync(appHostFile, cancellationToken));
        }

        // A project named like an AppHost may fail validation because it is not currently
        // buildable. Keep reporting the candidate so doctor can explain that the version is unknown,
        // but suppress ordinary projects that only matched the broad language detection patterns.
        return (validationResult.IsPossiblyUnbuildable, null);
    }

    private string GetRelativePath(FileInfo file)
    {
        return Path.GetRelativePath(executionContext.WorkingDirectory.FullName, file.FullName);
    }

    private static JsonObject BuildCliVersionMetadata(string currentVersion, string? latestVersion, string? updateCommand, string? updateCheckError)
    {
        var metadata = new JsonObject
        {
            ["currentVersion"] = currentVersion
        };

        if (!string.IsNullOrWhiteSpace(latestVersion))
        {
            metadata["latestVersion"] = latestVersion;
        }

        if (!string.IsNullOrWhiteSpace(updateCommand))
        {
            metadata["updateCommand"] = updateCommand;
        }

        if (!string.IsNullOrWhiteSpace(updateCheckError))
        {
            metadata["updateCheckError"] = updateCheckError;
        }

        return metadata;
    }

    private static JsonObject BuildAppHostVersionMetadata(string relativePath, string? version)
    {
        var metadata = new JsonObject
        {
            ["appHostPath"] = relativePath
        };

        if (!string.IsNullOrWhiteSpace(version))
        {
            metadata["version"] = version;
        }

        return metadata;
    }
}
