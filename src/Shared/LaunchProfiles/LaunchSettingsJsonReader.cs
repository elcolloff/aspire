// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Aspire.Hosting;

internal static class LaunchSettingsJsonReader
{
    internal static TLaunchSettings? GetLaunchSettingsFromDirectory<TLaunchSettings>(
        string? directoryPath,
        string resourceIdentifier,
        JsonTypeInfo<TLaunchSettings> jsonTypeInfo)
    {
        var launchSettingsFilePath = directoryPath is null
            ? Path.Combine("Properties", "launchSettings.json")
            : Path.Combine(Path.GetFullPath(directoryPath), "Properties", "launchSettings.json");

        if (!File.Exists(launchSettingsFilePath))
        {
            return default;
        }

        return ReadLaunchSettingsFile(launchSettingsFilePath, resourceIdentifier, jsonTypeInfo);
    }

    internal static TLaunchSettings? ReadLaunchSettingsFile<TLaunchSettings>(
        string launchSettingsFilePath,
        string resourceIdentifier,
        JsonTypeInfo<TLaunchSettings> jsonTypeInfo)
    {
        using var stream = File.OpenRead(launchSettingsFilePath);

        try
        {
            return JsonSerializer.Deserialize(stream, jsonTypeInfo);
        }
        catch (JsonException ex)
        {
            var message = $"Failed to get effective launch profile for {resourceIdentifier}. There is malformed JSON in the project's launch settings file at '{launchSettingsFilePath}'.";
            throw new InvalidDataException(message, ex);
        }
    }
}
