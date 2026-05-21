// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Aspire.Hosting;

/// <summary>
/// Low-level reader that deserializes a <c>launchSettings.json</c> file into a caller-supplied
/// type using the provided <see cref="JsonTypeInfo{TLaunchSettings}"/>. Unlike <c>LaunchSettingsReader</c>,
/// this reader does not depend on <c>Aspire.Hosting</c>'s <c>LaunchSettings</c> shape and surfaces
/// JSON parse failures as <see cref="InvalidDataException"/> so callers outside <c>Aspire.Hosting</c>
/// (for example the CLI, which uses its own <c>AppHostLaunchSettings</c> type) can adapt the error
/// without taking a dependency on <c>DistributedApplicationException</c>.
/// </summary>
internal static class LaunchSettingsJsonReader
{
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
