// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.Packaging;
using Aspire.Cli.Resources;
using Aspire.Shared;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Semver;
using Spectre.Console;

namespace Aspire.Cli.Projects;

internal interface IProjectUpdater
{
    Task<ProjectUpdateResult> UpdateProjectAsync(UpdatePackagesContext context, CancellationToken cancellationToken = default);
}

internal sealed partial class ProjectUpdater(ILogger<ProjectUpdater> logger, IDotNetCliRunner runner, IInteractionService interactionService, IMemoryCache cache, CliExecutionContext executionContext, FallbackProjectParser fallbackParser) : IProjectUpdater
{
    public async Task<ProjectUpdateResult> UpdateProjectAsync(UpdatePackagesContext context, CancellationToken cancellationToken = default)
    {
        var projectFile = context.AppHostFile;
        var channel = context.Channel;
        logger.LogDebug("Fetching '{AppHostPath}' items and properties.", projectFile.FullName);

        var (updateSteps, fallbackUsed) = await interactionService.ShowStatusAsync(UpdateCommandStrings.AnalyzingProjectStatus, () => GetUpdateStepsAsync(projectFile, channel, cancellationToken));

        if (!updateSteps.Any())
        {
            logger.LogInformation("No updates required for project: {ProjectFile}", projectFile.FullName);
            interactionService.DisplayMessage(KnownEmojis.CheckMarkButton, UpdateCommandStrings.ProjectUpToDateMessage);
            return new ProjectUpdateResult { UpdatedApplied = false };
        }

        interactionService.DisplayEmptyLine();

        // Group update steps by project for better visual organization
        var updateStepsByProject = updateSteps
            .OfType<PackageUpdateStep>()
            .GroupBy(step => step.ProjectFile.FullName)
            .ToList();

        // Display package updates grouped by project
        foreach (var projectGroup in updateStepsByProject)
        {
            var projectName = new FileInfo(projectGroup.Key).Name;
            if (updateStepsByProject.Count > 1)
            {
                interactionService.DisplayMessage(KnownEmojis.FileFolder, $"[bold cyan]{projectName.EscapeMarkup()}[/]:", allowMarkup: true);
            }

            foreach (var packageStep in projectGroup)
            {
                interactionService.DisplayMessage(KnownEmojis.Package, packageStep.GetFormattedDisplayText(), allowMarkup: true);
            }

            interactionService.DisplayEmptyLine();
        }

        // Display warning if fallback parsing was used
        if (fallbackUsed)
        {
            interactionService.DisplayMessage(KnownEmojis.Warning, $"[yellow]{UpdateCommandStrings.FallbackParsingWarning}[/]", allowMarkup: true);
            interactionService.DisplayEmptyLine();
        }

        if (!await interactionService.PromptConfirmAsync(UpdateCommandStrings.PerformUpdatesPrompt, context.ConfirmBinding, cancellationToken: cancellationToken))
        {
            return new ProjectUpdateResult { UpdatedApplied = false };
        }

        // Establish a baseline of NuGet diagnostics BEFORE applying updates so we can tell the
        // difference between issues that pre-existed (which the update may even heal) and issues
        // newly introduced by the update itself. The baseline is informational only — a failing
        // pre-restore is not fatal because the update may resolve it.
        NuGetRestoreResult? baseline = null;
        if (!context.SkipRestoreCheck)
        {
            baseline = await interactionService.ShowStatusAsync(
                UpdateCommandStrings.BaselineRestoreStatus,
                () => RunRestoreAndCaptureIssuesAsync(projectFile, cancellationToken));

            if (baseline.HasIssues)
            {
                logger.LogDebug(
                    "Baseline 'dotnet restore' for {ProjectFile} reported {Count} NuGet issue(s) (exitCode={ExitCode}). These will be excluded from the post-update regression check.",
                    projectFile.FullName,
                    baseline.Issues.Count,
                    baseline.ExitCode);
            }
        }

        if (channel.Type == PackageChannelType.Explicit)
        {
            var (configPathsExitCode, configPaths) = await runner.GetNuGetConfigPathsAsync(projectFile.Directory!, new(), cancellationToken);

            if (configPathsExitCode != 0 || configPaths is null || configPaths.Length == 0)
            {
                throw new ProjectUpdaterException(UpdateCommandStrings.FailedDiscoverNuGetConfig);
            }

            var configPathDirectories = configPaths.Select(Path.GetDirectoryName).ToArray();
            var fallbackNuGetConfigDirectory = executionContext.WorkingDirectory.FullName;

            // If there is one or zero config paths we assume that we should use
            // the fallback (there should always be one, but just for exhaustivenss).
            // If there is more than one we just make sure that the first on in the list
            // isn't a global config (on Windows with .NET and VS installed you'll have 3
            // global config files but the first one should be the NuGet in AppData).
            // The final rule should never ever be invoked, its just to get around CS8846
            // which does not evaluate when statements for exhaustiveness.
            var recommendedNuGetConfigFileDirectory = configPathDirectories switch
            {
                { Length: 0 or 1 } => fallbackNuGetConfigDirectory,
                var p when p.Length > 1 => IsGlobalNuGetConfig(p[0]!) ? fallbackNuGetConfigDirectory : p[0],

                // CS8846 error if we don't put this rule here even though we do "when"
                // above - this is corner case in C# evalutation of switch statements.
                _ => throw new InvalidOperationException(UpdateCommandStrings.UnexpectedCodePath)
            };

            interactionService.DisplayEmptyLine();

            // Carry the recommended directory as the default on the binding.
            // The original binding (from UpdateCommand) may not have a default because
            // the recommended directory is computed here, after NuGet config discovery.
            var nugetConfigDirBinding = context.NuGetConfigDirBinding.WithDefault(recommendedNuGetConfigFileDirectory);

            var selectedPathForNewNuGetConfigFile = await interactionService.PromptForFilePathAsync(
                promptText: UpdateCommandStrings.WhichDirectoryNuGetConfigPrompt,
                binding: nugetConfigDirBinding,
                validator: null,
                directory: true,
                required: true,
                cancellationToken: cancellationToken);

            var nugetConfigDirectory = new DirectoryInfo(selectedPathForNewNuGetConfigFile);

            // Snapshot the NuGet.config that NuGetConfigMerger may create or modify so it can be
            // rolled back on regression. If no existing file is found, snapshot the default
            // creation path ("nuget.config" in the target directory).
            if (!context.SkipRestoreCheck)
            {
                var nugetConfigPath = NuGetConfigMerger.TryFindNuGetConfigInDirectory(nugetConfigDirectory, out var existing)
                    ? existing.FullName
                    : Path.Combine(nugetConfigDirectory.FullName, "nuget.config");
                CaptureSnapshot(context, nugetConfigPath);
            }

            await NuGetConfigMerger.CreateOrUpdateAsync(nugetConfigDirectory, channel, (_, orig, proposed, ct) => AnalyzeAndConfirmNuGetConfigChanges(context, orig, proposed, ct), cancellationToken: cancellationToken);
        }

        // Take snapshots of all files the apply phase will touch so we can rollback if the
        // post-update restore reveals a regression. The set of mutated files is the union of:
        //   - the AppHost project file itself (SDK migration / SDK version updates),
        //   - every distinct PackageUpdateStep.ProjectFile (referenced projects updated by step),
        //   - the resolved Directory.Packages.props if CPM is in use,
        //   - the NuGet.config (handled above for Explicit channels).
        if (!context.SkipRestoreCheck)
        {
            CaptureSnapshot(context, projectFile.FullName);

            foreach (var stepProject in updateSteps.OfType<PackageUpdateStep>().Select(s => s.ProjectFile.FullName).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                CaptureSnapshot(context, stepProject);
            }

            var cpmInfo = DetectCentralPackageManagement(projectFile);
            if (cpmInfo.UsesCentralPackageManagement && cpmInfo.DirectoryPackagesPropsFile is not null)
            {
                CaptureSnapshot(context, cpmInfo.DirectoryPackagesPropsFile.FullName);
            }
        }

        interactionService.DisplayEmptyLine();

        await interactionService.ShowStatusAsync(
            UpdateCommandStrings.ApplyingUpdates,
            async () =>
            {
                foreach (var updateStep in updateSteps)
                {
                    interactionService.DisplaySubtleMessage(string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.ExecutingUpdateStepFormat, updateStep.Description));
                    await updateStep.Callback();
                }

                return 0;
            });

        interactionService.DisplayEmptyLine();

        // Validate the post-update state by running 'dotnet restore' again and comparing the
        // captured NuGet diagnostics against the baseline. Anything that wasn't in the baseline
        // is a regression introduced by this update — surface it and let the user choose.
        if (!context.SkipRestoreCheck && baseline is not null)
        {
            var post = await interactionService.ShowStatusAsync(
                UpdateCommandStrings.PostUpdateRestoreStatus,
                () => RunRestoreAndCaptureIssuesAsync(projectFile, cancellationToken));

            var newIssues = post.IssuesNotPresentIn(baseline);
            if (newIssues.Count > 0)
            {
                return await HandlePostRestoreRegressionAsync(context, newIssues, cancellationToken);
            }
        }

        interactionService.DisplaySuccess(UpdateCommandStrings.UpdateSuccessfulMessage);
        return new ProjectUpdateResult { UpdatedApplied = true };
    }

    /// <summary>
    /// Handles the case where post-update restore introduced issues that were not present in the
    /// baseline. In interactive mode the user is prompted to rollback or continue. In
    /// non-interactive mode we never auto-rollback (destructive); we surface the issues and throw
    /// so the command exits non-zero, mirroring the issue's design intent.
    /// </summary>
    private async Task<ProjectUpdateResult> HandlePostRestoreRegressionAsync(UpdatePackagesContext context, IReadOnlyList<NuGetRestoreIssue> newIssues, CancellationToken cancellationToken)
    {
        interactionService.DisplayEmptyLine();
        interactionService.DisplayMessage(KnownEmojis.Warning, $"[yellow]{UpdateCommandStrings.UpdateIntroducedNuGetIssuesHeader.EscapeMarkup()}[/]", allowMarkup: true);
        foreach (var issue in newIssues)
        {
            interactionService.DisplaySubtleMessage($"[yellow]{issue.RawLine.EscapeMarkup()}[/]", allowMarkup: true);
        }
        interactionService.DisplayEmptyLine();

        if (context.IsNonInteractive)
        {
            interactionService.DisplayError(UpdateCommandStrings.NonInteractiveNewIssuesMessage);
            throw new ProjectUpdaterException(UpdateCommandStrings.NonInteractiveNewIssuesMessage);
        }

        var rollback = await interactionService.PromptConfirmAsync(
            UpdateCommandStrings.RollbackUpdatesPrompt,
            PromptBinding.CreateDefault(true),
            cancellationToken: cancellationToken);

        if (!rollback)
        {
            interactionService.DisplayMessage(KnownEmojis.Warning, $"[yellow]{UpdateCommandStrings.ContinueWithIssuesMessage.EscapeMarkup()}[/]", allowMarkup: true);
            return new ProjectUpdateResult { UpdatedApplied = true };
        }

        await RollbackSnapshotsAsync(context);
        interactionService.DisplaySuccess(UpdateCommandStrings.RollbackSuccessMessage);
        return new ProjectUpdateResult { UpdatedApplied = false };
    }

    private static void CaptureSnapshot(UpdatePackagesContext context, string filePath)
    {
        var snapshots = GetOrCreateSnapshots(context);
        snapshots.Capture(filePath);
    }

    private async Task RollbackSnapshotsAsync(UpdatePackagesContext context)
    {
        var snapshots = GetOrCreateSnapshots(context);
        await snapshots.RollbackAsync(interactionService, logger);
    }

    // The snapshot set is stashed on the context so it survives across the explicit-channel branch
    // (which captures NuGet.config separately) and the apply phase.
    private static FileSnapshotSet GetOrCreateSnapshots(UpdatePackagesContext context)
    {
        if (s_snapshotSets.TryGetValue(context, out var existing))
        {
            return existing;
        }

        var created = new FileSnapshotSet();
        s_snapshotSets.Add(context, created);
        return created;
    }

    // ConditionalWeakTable lets us attach per-context state without modifying the public
    // UpdatePackagesContext shape further.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UpdatePackagesContext, FileSnapshotSet> s_snapshotSets = new();

    /// <summary>
    /// Runs <c>dotnet restore</c> against the AppHost project, capturing NuGet warning/error lines
    /// from stdout/stderr. Used both for the pre-update baseline and the post-update validation.
    /// Never throws on a non-zero exit — the caller decides how to interpret that.
    /// </summary>
    private async Task<NuGetRestoreResult> RunRestoreAndCaptureIssuesAsync(FileInfo projectFile, CancellationToken cancellationToken)
    {
        var issues = new List<NuGetRestoreIssue>();
        var seen = new HashSet<(string Code, string NormalizedLine)>();

        // NuGet diagnostics observed in the wild look like:
        //   /path/to/MyApp.AppHost.csproj : warning NU1605: Detected package downgrade: Foo from 9.0.4 to 9.0.0...
        //   error NU1101: Unable to find package 'Bar'. No packages exist with this id in source(s)...
        //   MyApp.csproj(12,3): error NU1605: Detected package downgrade...
        // The "warning|error NU####" token is the stable part across formats. We pull both the
        // code and the original line so we can both diff (by code+normalized line) and display
        // (raw line).
        void OnLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            var match = NuGetDiagnosticRegex().Match(line);
            if (!match.Success)
            {
                return;
            }

            var severity = string.Equals(match.Groups[1].Value, "error", StringComparison.OrdinalIgnoreCase)
                ? NuGetIssueSeverity.Error
                : NuGetIssueSeverity.Warning;
            var code = match.Groups[2].Value;
            var trimmed = line.Trim();
            var normalized = NormalizeForDiff(trimmed);

            if (seen.Add((code, normalized)))
            {
                issues.Add(new NuGetRestoreIssue(code, severity, trimmed, normalized));
            }
        }

        var options = new ProcessInvocationOptions
        {
            StandardOutputCallback = OnLine,
            StandardErrorCallback = OnLine,
        };

        int exitCode;
        try
        {
            exitCode = await runner.RestoreAsync(projectFile, options, cancellationToken);
        }
        catch (Exception ex)
        {
            // Treat an exception during restore the same as a failed exit so the baseline still
            // exists for diff purposes — a thrown post-restore would otherwise be uncomparable.
            logger.LogDebug(ex, "'dotnet restore' threw while validating {ProjectFile}", projectFile.FullName);
            exitCode = -1;
        }

        return new NuGetRestoreResult(exitCode, issues);
    }

    // Normalize to make the baseline-vs-post comparison resilient to incidental differences
    // (e.g. absolute paths in the prefix of the line). We strip the leading file-path token
    // before " : warning|error NUxxxx" if present, leaving just the diagnostic body.
    //
    //   "/repo/src/Foo.csproj : warning NU1605: Detected package downgrade: ..."
    //                       -> "warning NU1605: Detected package downgrade: ..."
    private static string NormalizeForDiff(string line)
    {
        var match = NuGetDiagnosticRegex().Match(line);
        return match.Success ? line[match.Index..].Trim() : line.Trim();
    }

    [GeneratedRegex(@"\b(warning|error)\s+(NU\d{4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex NuGetDiagnosticRegex();

    private static bool IsGlobalNuGetConfig(string path)
    {
        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
        {
            return path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        }
        else
        {
            var globalNuGetFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget");
            return path.StartsWith(globalNuGetFolder);
        }
    }

    private async Task<(IEnumerable<UpdateStep> UpdateSteps, bool FallbackUsed)> GetUpdateStepsAsync(FileInfo projectFile, PackageChannel channel, CancellationToken cancellationToken)
    {
        var context = new UpdateContext(projectFile, channel);

        var appHostAnalyzeStep = new AnalyzeStep(UpdateCommandStrings.AnalyzeAppHost, () => AnalyzeAppHostAsync(context, cancellationToken));
        context.AnalyzeSteps.Enqueue(appHostAnalyzeStep);

        while (context.AnalyzeSteps.TryDequeue(out var analyzeStep))
        {
            await analyzeStep.Callback();
        }

        return (context.UpdateSteps, context.FallbackParsing);
    }

    private const string ItemsAndPropertiesCacheKeyPrefix = "ItemsAndProperties";

    private async Task<JsonDocument> GetItemsAndPropertiesAsync(FileInfo projectFile, CancellationToken cancellationToken)
    {
        return await GetItemsAndPropertiesAsync(projectFile, ["PackageReference", "ProjectReference"], ["AspireHostingSDKVersion", "ManagePackageVersionsCentrally"], cancellationToken);
    }

    private async Task<JsonDocument> GetItemsAndPropertiesAsync(FileInfo projectFile, string[] items, string[] properties, CancellationToken cancellationToken)
    {
        // Create a cache key that includes the project file and the requested items/properties
        var itemsKey = string.Join(",", items.OrderBy(x => x));
        var propertiesKey = string.Join(",", properties.OrderBy(x => x));
        var cacheKey = $"{ItemsAndPropertiesCacheKeyPrefix}_{projectFile.FullName}_{itemsKey}_{propertiesKey}";

        var (exitCode, document) = await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            return await runner.GetProjectItemsAndPropertiesAsync(projectFile, items, properties, new(), cancellationToken);
        });

        if (exitCode != 0 || document is null)
        {
            throw new ProjectUpdaterException(string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.FailedFetchItemsAndPropertiesFormat, projectFile.FullName));
        }

        return document;
    }

    private async Task<JsonDocument> GetItemsAndPropertiesWithFallbackAsync(FileInfo projectFile, UpdateContext context, CancellationToken cancellationToken)
    {
        return await GetItemsAndPropertiesWithFallbackAsync(projectFile, ["PackageReference", "ProjectReference"], ["AspireHostingSDKVersion", "ManagePackageVersionsCentrally"], context, cancellationToken);
    }

    private async Task<JsonDocument> GetItemsAndPropertiesWithFallbackAsync(FileInfo projectFile, string[] items, string[] properties, UpdateContext context, CancellationToken cancellationToken)
    {
        try
        {
            // Try normal MSBuild evaluation first
            return await GetItemsAndPropertiesAsync(projectFile, items, properties, cancellationToken);
        }
        catch (ProjectUpdaterException ex) when (IsAppHostProject(projectFile, context))
        {
            // Only use fallback for AppHost projects
            logger.LogWarning("Falling back to parsing for '{ProjectFile}'. Reason: {Message}", projectFile.FullName, ex.Message);

            if (!context.FallbackParsing)
            {
                context.FallbackParsing = true;
                logger.LogWarning("Update plan will be generated using fallback parsing; dependency accuracy may be reduced.");
            }

            return fallbackParser.ParseProject(projectFile);
        }
    }

    private static bool IsAppHostProject(FileInfo projectFile, UpdateContext context)
    {
        return string.Equals(projectFile.FullName, context.AppHostProjectFile.FullName, StringComparison.OrdinalIgnoreCase);
    }

    private Task AnalyzeAppHostAsync(UpdateContext context, CancellationToken cancellationToken)
    {
        var appHostSdkAnalyzeStep = new AnalyzeStep(UpdateCommandStrings.AnalyzeAppHostSdk, () => AnalyzeAppHostSdkAsync(context, cancellationToken));
        context.AnalyzeSteps.Enqueue(appHostSdkAnalyzeStep);

        var appHostProjectAnalyzeStep = new AnalyzeStep(string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.AnalyzeProjectFormat, context.AppHostProjectFile.FullName), () => AnalyzeProjectAsync(context.AppHostProjectFile, context, cancellationToken));
        context.AnalyzeSteps.Enqueue(appHostProjectAnalyzeStep);

        return Task.CompletedTask;
    }

    private async Task<NuGetPackageCli?> GetLatestVersionOfPackageAsync(UpdateContext context, string packageId, CancellationToken cancellationToken, bool throwIfNotFound = true)
    {
        var cacheKey = $"LatestPackage-{packageId}";
        var latestPackage = await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            var packages = await context.Channel.GetPackagesAsync(packageId, context.AppHostProjectFile.Directory!, cancellationToken);
            // Filter out packages with invalid semantic versions and find the latest valid one
            var latestPackage = packages
                .Where(p => SemVersion.TryParse(p.Version, SemVersionStyles.Strict, out _))
                .OrderByDescending(p => SemVersion.Parse(p.Version, SemVersionStyles.Strict), SemVersion.PrecedenceComparer)
                .FirstOrDefault();
            return latestPackage;
        });

        if (latestPackage is null)
        {
            if (throwIfNotFound)
            {
                throw new ProjectUpdaterException(string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.NoPackageFoundFormat, packageId, context.Channel.Name));
            }

            logger.LogWarning(UpdateCommandStrings.PackageNotFoundInChannelWarningFormat, packageId, context.Channel.Name);
            return null;
        }

        return latestPackage;
    }

    private async Task AnalyzeAppHostSdkAsync(UpdateContext context, CancellationToken cancellationToken)
    {
        logger.LogDebug("Analyzing App Host SDK for: {AppHostFile}", context.AppHostProjectFile.FullName);

        var itemsAndPropertiesDocument = await GetItemsAndPropertiesWithFallbackAsync(context.AppHostProjectFile, context, cancellationToken);
        var propertiesElement = itemsAndPropertiesDocument.RootElement.GetProperty("Properties");
        var sdkVersionElement = propertiesElement.GetProperty("AspireHostingSDKVersion");
        var sdkVersion = sdkVersionElement.GetString();

        var latestSdkPackage = await GetLatestVersionOfPackageAsync(context, "Aspire.AppHost.Sdk", cancellationToken);

        // Treat unparseable versions (including range expressions) like wildcards - always update them
        // Only skip if the version is a valid semantic version that matches the latest
        if (!string.IsNullOrEmpty(sdkVersion) && IsValidSemanticVersion(sdkVersion) && sdkVersion == latestSdkPackage?.Version)
        {
            logger.LogInformation("App Host SDK is up to date.");

            // Even when the SDK version itself is current, the project may still
            // carry a stale Aspire.Hosting.AppHost PackageReference (csproj) or
            // PackageVersion (Directory.Packages.props). The new SDK implicitly
            // defines that PackageReference with IsImplicitlyDefined="true", so
            // an orphan PackageVersion entry causes NU1009 at restore time, and
            // an explicit PackageReference is now redundant. The SDK-update path
            // already cleans these up (in UpdateSdkVersionInProjectAppHostAsync);
            // do the same when no SDK version bump is required so a re-run of
            // `aspire update` can recover from a partial migration.
            // See https://github.com/microsoft/aspire/issues/15476.
            EnqueueLegacyAppHostCleanupStepIfNeeded(context);
            return;
        }

        // Detect what migration actions will be performed for .csproj files
        var migrationInfo = DetectMigrationActions(context.AppHostProjectFile);

        var sdkUpdateStep = new PackageUpdateStep(
            string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.UpdatePackageFormat, "Aspire.AppHost.Sdk", sdkVersion ?? "unknown", latestSdkPackage?.Version),
            () => UpdateSdkVersionInAppHostAsync(context.AppHostProjectFile, latestSdkPackage!, interactionService, migrationInfo),
            "Aspire.AppHost.Sdk",
            sdkVersion ?? "unknown",
            latestSdkPackage?.Version ?? "unknown",
            context.AppHostProjectFile);
        context.UpdateSteps.Enqueue(sdkUpdateStep);
    }

    private static void EnqueueLegacyAppHostCleanupStepIfNeeded(UpdateContext context)
    {
        var projectFile = context.AppHostProjectFile;

        // Only project-style AppHosts (.csproj/.fsproj/.vbproj) can carry these
        // legacy references. Single-file AppHosts (.cs with #:sdk directive)
        // never have a csproj or Directory.Packages.props to worry about.
        if (!ProjectFileExtensions.Supported.Contains(projectFile.Extension))
        {
            return;
        }

        var (hasPackageReference, hasOrphanPackageVersion) = DetectLegacyAppHostReferences(projectFile);
        if (!hasPackageReference && !hasOrphanPackageVersion)
        {
            return;
        }

        var step = new PackageUpdateStep(
            UpdateCommandStrings.RemovedObsoleteAppHostPackage,
            () => RemoveLegacyAppHostPackageReferencesAsync(projectFile, hasPackageReference, hasOrphanPackageVersion),
            "Aspire.Hosting.AppHost",
            // The new SDK adds Aspire.Hosting.AppHost implicitly, so there is no
            // user-visible "current version" to display - report it as implicit.
            "implicit",
            "(removed)",
            projectFile);
        context.UpdateSteps.Enqueue(step);
    }

    private static (bool HasPackageReference, bool HasOrphanPackageVersion) DetectLegacyAppHostReferences(FileInfo projectFile)
    {
        var hasPackageReference = false;
        var hasOrphanPackageVersion = false;

        try
        {
            var projectDocument = new XmlDocument { PreserveWhitespace = true };
            projectDocument.Load(projectFile.FullName);
            var projectNode = projectDocument.SelectSingleNode("/Project");
            if (projectNode is not null)
            {
                hasPackageReference = projectNode.SelectSingleNode(
                    CaseInsensitiveIncludeXPath("//PackageReference", "Aspire.Hosting.AppHost")) is not null;
            }
        }
        catch
        {
            // If we cannot parse the csproj here, leave detection to the build.
            // The SDK migration path is the primary cleanup; this is a defence
            // for the already-current path.
        }

        var cpmInfo = DetectCentralPackageManagement(projectFile);
        if (cpmInfo.UsesCentralPackageManagement && cpmInfo.DirectoryPackagesPropsFile is not null)
        {
            try
            {
                var propsDocument = new XmlDocument { PreserveWhitespace = true };
                propsDocument.Load(cpmInfo.DirectoryPackagesPropsFile.FullName);
                hasOrphanPackageVersion = propsDocument.SelectSingleNode(
                    CaseInsensitiveIncludeXPath("/Project/ItemGroup/PackageVersion", "Aspire.Hosting.AppHost")) is not null;
            }
            catch
            {
                // Same as above.
            }
        }

        return (hasPackageReference, hasOrphanPackageVersion);
    }

    private static async Task RemoveLegacyAppHostPackageReferencesAsync(
        FileInfo projectFile,
        bool removePackageReference,
        bool removePackageVersion)
    {
        if (removePackageReference)
        {
            var projectDocument = new XmlDocument { PreserveWhitespace = true };
            projectDocument.Load(projectFile.FullName);
            var projectNode = projectDocument.SelectSingleNode("/Project");
            if (projectNode is not null)
            {
                RemovePackageReference(projectNode, "Aspire.Hosting.AppHost");
                projectDocument.Save(projectFile.FullName);
            }
        }

        if (removePackageVersion)
        {
            // Reuses the same NU1009-aware cleanup the SDK migration path uses.
            // See UpdateSdkVersionInProjectAppHostAsync for the full rationale.
            RemovePackageVersionFromDirectoryPackagesProps(projectFile, "Aspire.Hosting.AppHost");
        }

        // The runner already displays the step's Description (which is
        // RemovedObsoleteAppHostPackage) via ExecutingUpdateStepFormat before
        // invoking this callback, so we deliberately do not emit the same
        // message a second time. The migration path in
        // UpdateSdkVersionInProjectAppHostAsync uses a different step
        // description (the SDK update format), which is why it can still emit
        // RemovedObsoleteAppHostPackage as a follow-up message there.
        await Task.CompletedTask;
    }

    private static SdkMigrationInfo DetectMigrationActions(FileInfo projectFile)
    {
        if (!string.Equals(projectFile.Extension, ".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return new SdkMigrationInfo(false, false);
        }

        try
        {
            var projectDocument = new XmlDocument();
            projectDocument.PreserveWhitespace = true;
            projectDocument.Load(projectFile.FullName);

            var projectNode = projectDocument.SelectSingleNode("/Project");
            if (projectNode is null)
            {
                return new SdkMigrationInfo(false, false);
            }

            // Check if using old SDK format (needs migration to new format)
            var sdkAttribute = projectNode.Attributes?["Sdk"];
            var usesOldFormat = sdkAttribute is null || !ContainsAspireAppHostSdk(sdkAttribute.Value);

            // Check if Aspire.Hosting.AppHost package reference exists (will be removed)
            var hasAppHostPackage = projectNode.SelectSingleNode(CaseInsensitiveIncludeXPath("//PackageReference", "Aspire.Hosting.AppHost")) is not null;

            return new SdkMigrationInfo(usesOldFormat, hasAppHostPackage);
        }
        catch
        {
            return new SdkMigrationInfo(false, false);
        }
    }

    private const string AspireAppHostSdkName = "Aspire.AppHost.Sdk";

    /// <summary>
    /// Checks if the Sdk attribute contains the Aspire.AppHost.Sdk.
    /// Handles formats like "Aspire.AppHost.Sdk/13.0.1" or "Aspire.AppHost.Sdk/13.0.1;Microsoft.NET.Sdk".
    /// </summary>
    private static bool ContainsAspireAppHostSdk(string sdkAttribute)
    {
        var sdks = sdkAttribute.Split(';');
        foreach (var sdk in sdks)
        {
            var trimmedSdk = sdk.Trim();

            // Check for exact match "Aspire.AppHost.Sdk" or "Aspire.AppHost.Sdk/version"
            if (trimmedSdk.Equals(AspireAppHostSdkName, StringComparison.OrdinalIgnoreCase) ||
                trimmedSdk.StartsWith(AspireAppHostSdkName + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Updates the Aspire.AppHost.Sdk version in the Sdk attribute, preserving any other SDKs.
    /// </summary>
    private static string UpdateAspireAppHostSdkVersion(string sdkAttribute, string newVersion)
    {
        var sdks = sdkAttribute.Split(';');
        var updatedSdks = new List<string>();

        foreach (var sdk in sdks)
        {
            var trimmedSdk = sdk.Trim();

            // Check if this is the Aspire.AppHost.Sdk
            if (trimmedSdk.Equals(AspireAppHostSdkName, StringComparison.OrdinalIgnoreCase) ||
                trimmedSdk.StartsWith(AspireAppHostSdkName + "/", StringComparison.OrdinalIgnoreCase))
            {
                // Replace with new version
                updatedSdks.Add($"{AspireAppHostSdkName}/{newVersion}");
            }
            else
            {
                updatedSdks.Add(trimmedSdk);
            }
        }

        return string.Join(";", updatedSdks);
    }

    internal static async Task UpdateSdkVersionInAppHostAsync(FileInfo projectFile, NuGetPackageCli package, IInteractionService interactionService, SdkMigrationInfo migrationInfo)
    {
        // Handles .cs|fs|vbproj files
        if (ProjectFileExtensions.Supported.Contains(projectFile.Extension))
        {
            await UpdateSdkVersionInProjectAppHostAsync(projectFile, package);

            // Display migration feedback messages
            if (migrationInfo.WillMigrateToNewFormat)
            {
                interactionService.DisplaySubtleMessage(string.Format(CultureInfo.InvariantCulture,
                    UpdateCommandStrings.MigratedToNewSdkFormat, package.Version));
            }

            if (migrationInfo.WillRemoveAppHostPackage)
            {
                interactionService.DisplaySubtleMessage(UpdateCommandStrings.RemovedObsoleteAppHostPackage);
            }
        }
        else if (string.Equals(projectFile.Extension, ".cs", StringComparison.OrdinalIgnoreCase))
        {
            await UpdateSdkVersionInSingleFileAppHostAsync(projectFile, package);
        }
        else
        {
            throw new ProjectUpdaterException(string.Format(CultureInfo.InvariantCulture,
                "Unsupported AppHost file type: {0}. Expected .csproj or .cs file.", projectFile.Extension));
        }
    }

    internal static async Task UpdateSdkVersionInProjectAppHostAsync(FileInfo projectFile, NuGetPackageCli package)
    {
        var projectDocument = new XmlDocument();
        projectDocument.PreserveWhitespace = true;

        projectDocument.Load(projectFile.FullName);

        var projectNode = projectDocument.SelectSingleNode("/Project");
        if (projectNode is null)
        {
            throw new ProjectUpdaterException(string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.CouldNotFindRootProjectElementFormat, projectFile.FullName));
        }

        // Check if the SDK is set via the Sdk attribute on the Project element (new format)
        var sdkAttribute = projectNode.Attributes?["Sdk"];
        if (sdkAttribute is not null && ContainsAspireAppHostSdk(sdkAttribute.Value))
        {
            // Already using new format: <Project Sdk="Aspire.AppHost.Sdk/version">
            // Update the version, preserving any other SDKs in the attribute
            sdkAttribute.Value = UpdateAspireAppHostSdkVersion(sdkAttribute.Value, package.Version);
        }
        else
        {
            // Migrate from old format to new format
            // Old format: <Sdk Name="Aspire.AppHost.Sdk" Version="..." />
            var sdkNode = projectNode.SelectSingleNode("Sdk[@Name='Aspire.AppHost.Sdk']");
            if (sdkNode is null)
            {
                throw new ProjectUpdaterException(string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.CouldNotFindSdkElementFormat, projectFile.FullName));
            }

            // Set the new format: <Project Sdk="Aspire.AppHost.Sdk/version">
            // The Aspire.AppHost.Sdk already includes the base .NET SDK, so we replace any existing SDK attribute
            if (sdkAttribute is not null)
            {
                sdkAttribute.Value = $"{AspireAppHostSdkName}/{package.Version}";
            }
            else
            {
                var newSdkAttribute = projectDocument.CreateAttribute("Sdk");
                newSdkAttribute.Value = $"{AspireAppHostSdkName}/{package.Version}";
                projectNode.Attributes!.SetNamedItem(newSdkAttribute);
            }

            // Remove the old <Sdk Name="Aspire.AppHost.Sdk" /> element and any surrounding whitespace
            RemoveNodeWithWhitespace(sdkNode);
        }

        // Remove the Aspire.Hosting.AppHost package reference if present (no longer needed with new SDK format)
        RemovePackageReference(projectNode, "Aspire.Hosting.AppHost");

        projectDocument.Save(projectFile.FullName);

        // The new SDK format adds an implicit PackageReference for Aspire.Hosting.AppHost with
        // IsImplicitlyDefined="true". NuGet's Central Package Management (CPM) rejects any
        // PackageVersion entry that targets an implicitly-defined package (NU1009). If the user
        // was previously managing this package through CPM, we must also remove the now-orphaned
        // PackageVersion entry from Directory.Packages.props to prevent the NU1009 error.
        RemovePackageVersionFromDirectoryPackagesProps(projectFile, "Aspire.Hosting.AppHost");

        await Task.CompletedTask;
    }

    // NuGet package IDs are case-insensitive (https://learn.microsoft.com/nuget/concepts/package-identifier),
    // so XPath lookups that match on @Include must compare case-insensitively. XPath 1.0 has no lower-case()
    // function, so we use translate() with the ASCII alphabet — package IDs are restricted to ASCII so this
    // is sufficient (https://learn.microsoft.com/nuget/reference/errors-and-warnings/nu1003).
    private const string AsciiUpper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string AsciiLower = "abcdefghijklmnopqrstuvwxyz";

    private static string CaseInsensitiveIncludeXPath(string axisAndElement, string packageId)
        => $"{axisAndElement}[translate(@Include, '{AsciiUpper}', '{AsciiLower}')='{packageId.ToLowerInvariant()}']";

    private static void RemoveNodeWithWhitespace(XmlNode node)
    {
        var parent = node.ParentNode;
        if (parent is null)
        {
            return;
        }

        // Remove preceding whitespace (any text/whitespace node type)
        var previousSibling = node.PreviousSibling;
        if (previousSibling is XmlCharacterData charData && string.IsNullOrWhiteSpace(charData.Data))
        {
            parent.RemoveChild(previousSibling);
        }

        parent.RemoveChild(node);
    }

    private static void RemovePackageReference(XmlNode projectNode, string packageId)
    {
        var packageNode = projectNode.SelectSingleNode(CaseInsensitiveIncludeXPath("//PackageReference", packageId));
        if (packageNode?.ParentNode is null)
        {
            return;
        }

        var parentNode = packageNode.ParentNode;

        // Remove the package reference and any preceding whitespace
        RemoveNodeWithWhitespace(packageNode);

        // If ItemGroup is now empty (only whitespace), remove it too
        if (parentNode.Name == "ItemGroup" && IsEmptyOrWhitespace(parentNode))
        {
            RemoveNodeWithWhitespace(parentNode);
        }
    }

    private static bool IsEmptyOrWhitespace(XmlNode node)
    {
        foreach (XmlNode child in node.ChildNodes)
        {
            // Check for any type of text/whitespace node (XmlText, XmlWhitespace, XmlSignificantWhitespace)
            if (child is XmlCharacterData charData)
            {
                if (!string.IsNullOrWhiteSpace(charData.Data))
                {
                    return false;
                }
            }
            else
            {
                // Non-text node found (element, comment, etc.)
                return false;
            }
        }
        return true;
    }

    private static void RemovePackageVersionFromDirectoryPackagesProps(FileInfo projectFile, string packageId)
    {
        var cpmInfo = DetectCentralPackageManagement(projectFile);
        if (!cpmInfo.UsesCentralPackageManagement || cpmInfo.DirectoryPackagesPropsFile is null)
        {
            return;
        }

        try
        {
            var propsDocument = new XmlDocument();
            propsDocument.PreserveWhitespace = true;
            propsDocument.Load(cpmInfo.DirectoryPackagesPropsFile.FullName);

            var packageVersionNode = propsDocument.SelectSingleNode(CaseInsensitiveIncludeXPath("/Project/ItemGroup/PackageVersion", packageId));
            if (packageVersionNode?.ParentNode is null)
            {
                return;
            }

            var parentNode = packageVersionNode.ParentNode;

            RemoveNodeWithWhitespace(packageVersionNode);

            if (parentNode.Name == "ItemGroup" && IsEmptyOrWhitespace(parentNode))
            {
                RemoveNodeWithWhitespace(parentNode);
            }

            propsDocument.Save(cpmInfo.DirectoryPackagesPropsFile.FullName);
        }
        catch (Exception ex)
        {
            // The csproj has already been updated at this point, so we can't roll back.
            // Inform the user what manual step is needed to complete the migration.
            throw new ProjectUpdaterException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "The project file was updated successfully, but the PackageVersion entry for '{0}' could not be " +
                    "removed from '{1}': {2}. Please manually remove the <PackageVersion Include=\"{0}\" ... /> " +
                    "entry from this file to avoid NU1009 build errors.",
                    packageId,
                    cpmInfo.DirectoryPackagesPropsFile.FullName,
                    ex.Message),
                ex);
        }
    }

    private static async Task UpdateSdkVersionInSingleFileAppHostAsync(FileInfo projectFile, NuGetPackageCli package)
    {
        var fileContent = await File.ReadAllTextAsync(projectFile.FullName);

        // Look for the #:sdk Aspire.AppHost.Sdk@<version> directive
        var match = SdkDirectiveRegex().Match(fileContent);

        if (!match.Success)
        {
            throw new ProjectUpdaterException(string.Format(CultureInfo.InvariantCulture,
                "Could not find '#:sdk Aspire.AppHost.Sdk@<version>' directive in single-file AppHost: {0}", projectFile.FullName));
        }

        // Replace the matched SDK directive with the new version
        var newDirective = $"#:sdk Aspire.AppHost.Sdk@{package.Version}";
        var updatedContent = SdkDirectiveRegex().Replace(fileContent, newDirective, 1);

        await File.WriteAllTextAsync(projectFile.FullName, updatedContent);
    }

    [GeneratedRegex(@"#:sdk\s+Aspire\.AppHost\.Sdk@(?:[\d\.\-a-zA-Z]+|\*)")]
    internal static partial Regex SdkDirectiveRegex();

    private async Task AnalyzeProjectAsync(FileInfo projectFile, UpdateContext context, CancellationToken cancellationToken)
    {
        if (!context.VisitedProjects.Add(projectFile.FullName))
        {
            // Project already analyzed, skip
            return;
        }

        // Use fallback wrapper for AppHost project, normal method for others
        var itemsAndPropertiesDocument = IsAppHostProject(projectFile, context)
            ? await GetItemsAndPropertiesWithFallbackAsync(projectFile, context, cancellationToken)
            : await GetItemsAndPropertiesAsync(projectFile, cancellationToken);

        // Check if this project has ManagePackageVersionsCentrally set to false
        var usesCentralPackageManagement = true;
        if (itemsAndPropertiesDocument.RootElement.TryGetProperty("Properties", out var propertiesElement))
        {
            if (propertiesElement.TryGetProperty("ManagePackageVersionsCentrally", out var managePkgVersionsElement))
            {
                var managePkgVersionsValue = managePkgVersionsElement.GetString();
                // If the property is explicitly set to false, don't use CPM even if Directory.Packages.props exists
                if (string.Equals(managePkgVersionsValue, "false", StringComparison.OrdinalIgnoreCase))
                {
                    usesCentralPackageManagement = false;
                }
            }
        }

        // Detect if this project uses Central Package Management (if not already disabled by property)
        var cpmInfo = usesCentralPackageManagement
            ? DetectCentralPackageManagement(projectFile)
            : new CentralPackageManagementInfo(false, null);

        var itemsElement = itemsAndPropertiesDocument.RootElement.GetProperty("Items");

        // Handle ProjectReference items (may not exist if project has no project references)
        if (itemsElement.TryGetProperty("ProjectReference", out var projectReferencesElement))
        {
            foreach (var projectReference in projectReferencesElement.EnumerateArray())
            {
                var referencedProjectPath = projectReference.GetProperty("FullPath").GetString() ?? throw new ProjectUpdaterException(UpdateCommandStrings.ProjectReferenceNoFullPath);
                var referencedProjectFile = new FileInfo(referencedProjectPath);
                context.AnalyzeSteps.Enqueue(new AnalyzeStep(string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.AnalyzeProjectFormat, referencedProjectFile.FullName), () => AnalyzeProjectAsync(referencedProjectFile, context, cancellationToken)));
            }
        }

        // Handle PackageReference items (may not exist if project has no package references)
        if (itemsElement.TryGetProperty("PackageReference", out var packageReferencesElement))
        {
            foreach (var packageReference in packageReferencesElement.EnumerateArray())
            {
            var packageId = packageReference.GetProperty("Identity").GetString() ?? throw new ProjectUpdaterException(UpdateCommandStrings.PackageReferenceNoIdentity);

            if (!IsUpdatablePackage(packageId))
            {
                continue;
            }

            if (cpmInfo.UsesCentralPackageManagement)
            {
                await AnalyzePackageForCentralPackageManagementAsync(packageId, projectFile, cpmInfo.DirectoryPackagesPropsFile!, context, cancellationToken);
            }
            else
            {
                // Traditional package management - Version should be in PackageReference
                if (!packageReference.TryGetProperty("Version", out var versionElement))
                {
                    // Version attribute is missing - treat as wildcard
                    var packageVersion = "*";
                    await AnalyzePackageForTraditionalManagementAsync(packageId, packageVersion, projectFile, context, cancellationToken);
                }
                else
                {
                    var packageVersion = versionElement.GetString();
                    if (string.IsNullOrEmpty(packageVersion) || packageVersion == "*")
                    {
                        // Version is * or empty - treat as wildcard
                        packageVersion = "*";
                    }
                    await AnalyzePackageForTraditionalManagementAsync(packageId, packageVersion, projectFile, context, cancellationToken);
                }
            }
        }
    }
    }

    private static bool IsUpdatablePackage(string packageId)
    {
        // Skip Aspire.Hosting.AppHost - it's removed during SDK update (no longer needed with new SDK format)
        if (string.Equals(packageId, "Aspire.Hosting.AppHost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return packageId.StartsWith("Aspire.", StringComparison.Ordinal);
    }

    private static CentralPackageManagementInfo DetectCentralPackageManagement(FileInfo projectFile)
    {
        // Look for Directory.Packages.props in directory tree.
        for (var current = projectFile.Directory; current is not null; current = current.Parent)
        {
            var directoryPackagesPropsPath = Path.Combine(current.FullName, "Directory.Packages.props");
            if (File.Exists(directoryPackagesPropsPath))
            {
                return new CentralPackageManagementInfo(true, new FileInfo(directoryPackagesPropsPath));
            }
        }

        return new CentralPackageManagementInfo(false, null);
    }

    private async Task AnalyzePackageForTraditionalManagementAsync(string packageId, string packageVersion, FileInfo projectFile, UpdateContext context, CancellationToken cancellationToken)
    {
        var latestPackage = await GetLatestVersionOfPackageAsync(context, packageId, cancellationToken, throwIfNotFound: false);

        if (latestPackage is null)
        {
            // Package was not found in the channel; a warning has already been logged. Skip this package.
            return;
        }

        // Treat unparseable versions (including range expressions) like wildcards - always update them
        // Only skip if the version is a valid semantic version that matches the latest
        if (IsValidSemanticVersion(packageVersion) && packageVersion == latestPackage.Version)
        {
            logger.LogInformation("Package '{PackageId}' is up to date.", packageId);
            return;
        }

        var updateStep = new PackageUpdateStep(
            string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.UpdatePackageFormat, packageId, packageVersion, latestPackage.Version),
            () => UpdatePackageReferenceInProject(projectFile, latestPackage, cancellationToken),
            packageId,
            packageVersion,
            latestPackage.Version,
            projectFile);
        context.UpdateSteps.Enqueue(updateStep);
    }

    private async Task AnalyzePackageForCentralPackageManagementAsync(string packageId, FileInfo projectFile, FileInfo directoryPackagesPropsFile, UpdateContext context, CancellationToken cancellationToken)
    {
        var currentVersion = await GetPackageVersionFromDirectoryPackagesPropsAsync(packageId, directoryPackagesPropsFile, projectFile, cancellationToken);

        if (currentVersion is null)
        {
            logger.LogInformation("Package '{PackageId}' not found in Directory.Packages.props, skipping.", packageId);
            return;
        }

        var latestPackage = await GetLatestVersionOfPackageAsync(context, packageId, cancellationToken, throwIfNotFound: false);

        if (latestPackage is null)
        {
            // Package was not found in the channel; a warning has already been logged. Skip this package.
            return;
        }

        // Treat unparseable versions (including range expressions) like wildcards - always update them
        // Only skip if the version is a valid semantic version that matches the latest
        if (IsValidSemanticVersion(currentVersion) && currentVersion == latestPackage.Version)
        {
            logger.LogInformation("Package '{PackageId}' is up to date.", packageId);
            return;
        }

        var updateStep = new PackageUpdateStep(
            string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.UpdatePackageFormat, packageId, currentVersion, latestPackage.Version),
            () => UpdatePackageVersionInDirectoryPackagesProps(packageId, latestPackage.Version, directoryPackagesPropsFile),
            packageId,
            currentVersion,
            latestPackage.Version,
            projectFile);
        context.UpdateSteps.Enqueue(updateStep);
    }

    private async Task<string?> GetPackageVersionFromDirectoryPackagesPropsAsync(string packageId, FileInfo directoryPackagesPropsFile, FileInfo projectFile, CancellationToken cancellationToken)
    {
        try
        {
            var doc = new XmlDocument { PreserveWhitespace = true };
            doc.Load(directoryPackagesPropsFile.FullName);
            var packageVersionNode = doc.SelectSingleNode(CaseInsensitiveIncludeXPath("/Project/ItemGroup/PackageVersion", packageId));
            var versionAttribute = packageVersionNode?.Attributes?["Version"]?.Value;

            if (versionAttribute is null)
            {
                return null;
            }

            // Check if this is an MSBuild property expression like $(AspireVersion)
            if (IsMSBuildPropertyExpression(versionAttribute))
            {
                var propertyName = ExtractPropertyNameFromExpression(versionAttribute);
                if (propertyName is not null)
                {
                    var resolvedValue = await ResolveMSBuildPropertyAsync(propertyName, projectFile, cancellationToken);
                    if (resolvedValue is not null && IsValidSemanticVersion(resolvedValue))
                    {
                        return resolvedValue;
                    }
                    else
                    {
                        throw new ProjectUpdaterException(string.Format(CultureInfo.InvariantCulture,
                            "Unable to resolve MSBuild property '{0}' to a valid semantic version. Expression: '{1}', Resolved value: '{2}'",
                            propertyName, versionAttribute, resolvedValue ?? "null"));
                    }
                }
                else
                {
                    throw new ProjectUpdaterException(string.Format(CultureInfo.InvariantCulture,
                        "Invalid MSBuild property expression in package version: '{0}'", versionAttribute));
                }
            }

            return versionAttribute;
        }
        catch (ProjectUpdaterException)
        {
            // Re-throw our custom exceptions
            throw;
        }
        catch (Exception ex)
        {
            // Ignore parse errors.
            logger.LogInformation(ex, "Ignoring parsing error in Directory.Packages.props '{DirectoryPackagesPropsFile}' for project '{ProjectFile}'", directoryPackagesPropsFile.FullName, projectFile.FullName);
            return null;
        }
    }

    private static bool IsMSBuildPropertyExpression(string value)
    {
        return value.StartsWith("$(") && value.EndsWith(")") && value.Length > 3;
    }

    private static string? ExtractPropertyNameFromExpression(string expression)
    {
        if (!IsMSBuildPropertyExpression(expression))
        {
            return null;
        }

        // Extract property name from $(PropertyName)
        return expression.Substring(2, expression.Length - 3);
    }

    private async Task<string?> ResolveMSBuildPropertyAsync(string propertyName, FileInfo projectFile, CancellationToken cancellationToken)
    {
        try
        {
            var document = await GetItemsAndPropertiesAsync(
                projectFile,
                Array.Empty<string>(), // No items needed
                [propertyName], // Just the property we want
                cancellationToken);

            var propertiesElement = document.RootElement.GetProperty("Properties");
            if (propertiesElement.TryGetProperty(propertyName, out var propertyElement))
            {
                return propertyElement.GetString();
            }

            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Exception while resolving MSBuild property '{PropertyName}' for project '{ProjectFile}'", propertyName, projectFile.FullName);
            return null;
        }
    }

    private static bool IsValidSemanticVersion(string version)
    {
        return SemVersion.TryParse(version, SemVersionStyles.Strict, out _);
    }

    private static async Task UpdatePackageVersionInDirectoryPackagesProps(string packageId, string newVersion, FileInfo directoryPackagesPropsFile)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.Load(directoryPackagesPropsFile.FullName);

        var packageVersionNode = doc.SelectSingleNode(CaseInsensitiveIncludeXPath("/Project/ItemGroup/PackageVersion", packageId));
        if (packageVersionNode?.Attributes?["Version"] is null)
        {
            throw new ProjectUpdaterException(string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.CouldNotFindPackageVersionInDirectoryPackagesProps, packageId, directoryPackagesPropsFile.FullName));
        }

        packageVersionNode.Attributes["Version"]!.Value = newVersion;
        doc.Save(directoryPackagesPropsFile.FullName);

        await Task.CompletedTask;
    }

    private async Task UpdatePackageReferenceInProject(FileInfo projectFile, NuGetPackageCli package, CancellationToken cancellationToken)
    {
        var exitCode = await runner.AddPackageAsync(
            projectFilePath: projectFile,
            packageName: package.Id,
            packageVersion: package.Version,
            nugetSource: null,
            noRestore: false,
            options: new(),
            cancellationToken: cancellationToken);

        if (exitCode != 0)
        {
            throw new ProjectUpdaterException(string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.FailedUpdatePackageReferenceFormat, package.Id, projectFile.FullName));
        }
    }

    private async Task<bool> AnalyzeAndConfirmNuGetConfigChanges(UpdatePackagesContext context, XmlDocument? originalDocument, XmlDocument proposedDocument, CancellationToken cancellationToken)
    {
        interactionService.DisplayEmptyLine();

        var changes = AnalyzeNuGetConfigChanges(originalDocument, proposedDocument);

        if (!changes.HasChanges)
        {
            interactionService.DisplayPlainText(UpdateCommandStrings.NoChangesDetectedInNuGetConfig);
            return true;
        }

        DisplayNuGetConfigChanges(changes);

        var shouldProceed = await interactionService.PromptConfirmAsync(
            UpdateCommandStrings.ApplyChangesToNuGetConfig,
            binding: context.ConfirmBinding,
            cancellationToken: cancellationToken);

        return shouldProceed;
    }

    private static NuGetConfigChanges AnalyzeNuGetConfigChanges(XmlDocument? originalDocument, XmlDocument proposedDocument)
    {
        var changes = new NuGetConfigChanges();

        // Extract package sources from both documents
        var originalSources = ExtractPackageSources(originalDocument);
        var proposedSources = ExtractPackageSources(proposedDocument);

        // Analyze feed changes
        changes.AddedFeeds = proposedSources.Where(p => !originalSources.Any(o => o.Key == p.Key)).ToList();
        changes.RemovedFeeds = originalSources.Where(o => !proposedSources.Any(p => p.Key == o.Key)).ToList();
        changes.RetainedFeeds = originalSources.Where(o => proposedSources.Any(p => p.Key == o.Key)).ToList();

        // Extract package source mappings from both documents
        var originalMappings = ExtractPackageSourceMappings(originalDocument);
        var proposedMappings = ExtractPackageSourceMappings(proposedDocument);

        // Store mappings for display
        changes.OriginalMappings = originalMappings;
        changes.ProposedMappings = proposedMappings;

        // Analyze mapping changes
        changes.MappingChanges = AnalyzeMappingChanges(originalMappings, proposedMappings);

        return changes;
    }

    private static List<PackageSourceInfo> ExtractPackageSources(XmlDocument? document)
    {
        var sources = new List<PackageSourceInfo>();
        if (document?.DocumentElement == null)
        {
            return sources;
        }

        var packageSources = document.DocumentElement.SelectSingleNode("packageSources");
        if (packageSources != null)
        {
            var addNodes = packageSources.SelectNodes("add");
            if (addNodes != null)
            {
                foreach (XmlNode addNode in addNodes)
                {
                    var key = addNode.Attributes?["key"]?.Value;
                    var value = addNode.Attributes?["value"]?.Value;
                    if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                    {
                        sources.Add(new PackageSourceInfo(key, value));
                    }
                }
            }
        }

        return sources;
    }

    private static Dictionary<string, List<string>> ExtractPackageSourceMappings(XmlDocument? document)
    {
        var mappings = new Dictionary<string, List<string>>();
        if (document?.DocumentElement == null)
        {
            return mappings;
        }

        var packageSourceMapping = document.DocumentElement.SelectSingleNode("packageSourceMapping");
        if (packageSourceMapping != null)
        {
            var packageSourceNodes = packageSourceMapping.SelectNodes("packageSource");
            if (packageSourceNodes != null)
            {
                foreach (XmlNode packageSourceNode in packageSourceNodes)
                {
                    var sourceKey = packageSourceNode.Attributes?["key"]?.Value;
                    if (!string.IsNullOrEmpty(sourceKey))
                    {
                        var patterns = new List<string>();
                        var packageNodes = packageSourceNode.SelectNodes("package");
                        if (packageNodes != null)
                        {
                            foreach (XmlNode packageNode in packageNodes)
                            {
                                var pattern = packageNode.Attributes?["pattern"]?.Value;
                                if (!string.IsNullOrEmpty(pattern))
                                {
                                    patterns.Add(pattern);
                                }
                            }
                        }
                        mappings[sourceKey] = patterns;
                    }
                }
            }
        }

        return mappings;
    }

    private static List<MappingChange> AnalyzeMappingChanges(Dictionary<string, List<string>> originalMappings, Dictionary<string, List<string>> proposedMappings)
    {
        var changes = new List<MappingChange>();

        // Find sources with mapping changes
        var allSources = originalMappings.Keys.Union(proposedMappings.Keys).ToHashSet();

        foreach (var source in allSources)
        {
            var originalPatterns = originalMappings.GetValueOrDefault(source, []);
            var proposedPatterns = proposedMappings.GetValueOrDefault(source, []);

            var addedPatterns = proposedPatterns.Except(originalPatterns).ToList();
            var removedPatterns = originalPatterns.Except(proposedPatterns).ToList();

            if (addedPatterns.Count > 0 || removedPatterns.Count > 0)
            {
                changes.Add(new MappingChange(source, addedPatterns, removedPatterns));
            }
        }

        return changes;
    }

    private void DisplayNuGetConfigChanges(NuGetConfigChanges changes)
    {
        // Create a lookup of mapping changes by source for quick access
        var mappingChangesBySource = changes.MappingChanges.ToDictionary(mc => mc.SourceKey, mc => mc);

        // Display added feeds with their mappings
        foreach (var feed in changes.AddedFeeds)
        {
            interactionService.DisplayPlainText(string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.AddedFeedFormat, feed.Value));
            interactionService.DisplayEmptyLine();

            if (changes.ProposedMappings.TryGetValue(feed.Key, out var patterns))
            {
                foreach (var pattern in patterns)
                {
                    interactionService.DisplayPlainText(string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.MappingAddedFormat, pattern));
                }
            }
            interactionService.DisplayEmptyLine();
        }

        // Display removed feeds
        foreach (var feed in changes.RemovedFeeds)
        {
            interactionService.DisplayPlainText(string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.RemovedFeedFormat, feed.Value));
            interactionService.DisplayEmptyLine();
        }

        // Display retained feeds with their mapping changes and current mappings
        foreach (var feed in changes.RetainedFeeds)
        {
            interactionService.DisplayPlainText(string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.RetainedFeedFormat, feed.Value));
            interactionService.DisplayEmptyLine();

            if (mappingChangesBySource.TryGetValue(feed.Key, out var mappingChange))
            {
                // Show added patterns
                foreach (var pattern in mappingChange.AddedPatterns)
                {
                    interactionService.DisplayPlainText(string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.MappingAddedFormat, pattern));
                }

                // Show removed patterns
                foreach (var pattern in mappingChange.RemovedPatterns)
                {
                    interactionService.DisplayPlainText(string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.MappingRemovedFormat, pattern));
                }
            }

            // Show current/unchanged mappings in the proposed configuration
            if (changes.ProposedMappings.TryGetValue(feed.Key, out var currentPatterns))
            {
                var addedPatterns = mappingChangesBySource.TryGetValue(feed.Key, out var currentMappingChange) ? currentMappingChange.AddedPatterns : new List<string>();

                foreach (var pattern in currentPatterns)
                {
                    // Only show patterns that weren't added (they are existing/unchanged)
                    if (!addedPatterns.Contains(pattern))
                    {
                        interactionService.DisplayPlainText(string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.MappingRetainedFormat, pattern));
                    }
                }
            }
            interactionService.DisplayEmptyLine();
        }
    }
}

internal record PackageSourceInfo(string Key, string Value);

internal record MappingChange(string SourceKey, List<string> AddedPatterns, List<string> RemovedPatterns);

internal class NuGetConfigChanges
{
    public List<PackageSourceInfo> AddedFeeds { get; set; } = [];
    public List<PackageSourceInfo> RemovedFeeds { get; set; } = [];
    public List<PackageSourceInfo> RetainedFeeds { get; set; } = [];
    public List<MappingChange> MappingChanges { get; set; } = [];
    public Dictionary<string, List<string>> OriginalMappings { get; set; } = new();
    public Dictionary<string, List<string>> ProposedMappings { get; set; } = new();

    public bool HasChanges => AddedFeeds.Count > 0 || RemovedFeeds.Count > 0 || MappingChanges.Count > 0;
}

internal sealed class ProjectUpdateResult
{
    public bool UpdatedApplied { get; set; }
}

internal sealed class UpdateContext(FileInfo appHostProjectFile, PackageChannel channel)
{
    public FileInfo AppHostProjectFile { get; } = appHostProjectFile;
    public PackageChannel Channel { get; } = channel;
    public ConcurrentQueue<UpdateStep> UpdateSteps { get; } = new();
    public ConcurrentQueue<AnalyzeStep> AnalyzeSteps { get; } = new();
    public HashSet<string> VisitedProjects { get; } = new();
    public bool FallbackParsing { get; set; }
}

internal abstract record UpdateStep(string Description, Func<Task> Callback)
{
    /// <summary>
    /// Gets the formatted display text using Spectre Console markup for enhanced visual presentation.
    /// </summary>
    public virtual string GetFormattedDisplayText() => Description;
}

/// <summary>
/// Represents an update step for a package reference, containing package and project information.
/// </summary>
internal record PackageUpdateStep(
    string Description,
    Func<Task> Callback,
    string PackageId,
    string CurrentVersion,
    string NewVersion,
    FileInfo ProjectFile) : UpdateStep(Description, Callback)
{
    public override string GetFormattedDisplayText()
    {
        return $"[bold yellow]{PackageId.EscapeMarkup()}[/] [bold green]{CurrentVersion.EscapeMarkup()}[/] to [bold green]{NewVersion.EscapeMarkup()}[/]";
    }
}

internal record AnalyzeStep(string Description, Func<Task> Callback);

internal sealed class ProjectUpdaterException : System.Exception
{
    public ProjectUpdaterException(string message) : base(message) { }
    public ProjectUpdaterException(string message, System.Exception inner) : base(message, inner) { }
}

internal record CentralPackageManagementInfo(bool UsesCentralPackageManagement, FileInfo? DirectoryPackagesPropsFile);

internal record SdkMigrationInfo(bool WillMigrateToNewFormat, bool WillRemoveAppHostPackage);

internal enum NuGetIssueSeverity { Warning, Error }

internal sealed record NuGetRestoreIssue(string Code, NuGetIssueSeverity Severity, string RawLine, string NormalizedLine);

internal sealed record NuGetRestoreResult(int ExitCode, IReadOnlyList<NuGetRestoreIssue> Issues)
{
    public bool HasIssues => Issues.Count > 0 || ExitCode != 0;

    /// <summary>
    /// Returns the issues in this result that are not present in <paramref name="baseline"/> by
    /// (Code, NormalizedLine). If <paramref name="baseline"/> reported a non-zero exit code with no
    /// captured issues (i.e. restore was already broken in some non-NuGet way) then a non-zero
    /// post-restore exit alone is not treated as a regression.
    /// </summary>
    public IReadOnlyList<NuGetRestoreIssue> IssuesNotPresentIn(NuGetRestoreResult baseline)
    {
        var baselineKeys = baseline.Issues.Select(i => (i.Code, i.NormalizedLine)).ToHashSet();
        return Issues.Where(i => !baselineKeys.Contains((i.Code, i.NormalizedLine))).ToList();
    }
}

/// <summary>
/// In-memory file snapshot captured before the apply phase of <see cref="ProjectUpdater"/>.
/// On <see cref="RollbackAsync"/> originals are written back, or files that did not previously
/// exist are deleted. Errors during restore are surfaced per-file so a single failure does not
/// abort the rest of the rollback.
/// </summary>
internal sealed class FileSnapshotSet
{
    private readonly Dictionary<string, byte[]?> _snapshots = new(StringComparer.OrdinalIgnoreCase);

    public void Capture(string path)
    {
        if (string.IsNullOrEmpty(path) || _snapshots.ContainsKey(path))
        {
            return;
        }

        try
        {
            _snapshots[path] = File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception)
        {
            // If we can't read the file, don't capture a snapshot — rollback for this path becomes
            // a no-op rather than risking writing wrong bytes back later.
            _snapshots[path] = null;
        }
    }

    public async Task RollbackAsync(IInteractionService interactionService, ILogger logger)
    {
        foreach (var (path, original) in _snapshots)
        {
            try
            {
                if (original is null)
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                else
                {
                    await File.WriteAllBytesAsync(path, original);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to rollback file: {Path}", path);
                interactionService.DisplayMessage(
                    KnownEmojis.Warning,
                    string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.RollbackFailedFormat, path, ex.Message));
            }
        }
    }
}
