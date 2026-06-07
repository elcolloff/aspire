// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001
#pragma warning disable ASPIREUSERSECRETS001

using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.UserSecrets;

internal sealed class IsolatedUserSecretsManager(IUserSecretsManager inner, TempDirectory directory) : IUserSecretsManager, IDisposable, IAsyncDisposable
{
    private bool _disposed;

    public bool IsAvailable => inner.IsAvailable;

    public string FilePath => inner.FilePath;

    public bool TrySetSecret(string name, string value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return inner.TrySetSecret(name, value);
    }

    public bool TryDeleteSecret(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return inner.TryDeleteSecret(name);
    }

    public void GetOrSetSecret(IConfigurationManager configuration, string name, Func<string> valueGenerator)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        inner.GetOrSetSecret(configuration, name, valueGenerator);
    }

    public Task SaveStateAsync(JsonObject state, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return inner.SaveStateAsync(state, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        directory.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
