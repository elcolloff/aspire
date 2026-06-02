// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Tests;

public class GracefulShutdownServiceTests
{
    [Fact]
    public void Token_BeforeExpire_NotCancelled()
    {
        using var service = new GracefulShutdownService();

        Assert.False(service.Token.IsCancellationRequested);
    }

    [Fact]
    public void Expire_FiresToken()
    {
        using var service = new GracefulShutdownService();

        service.Expire();

        Assert.True(service.Token.IsCancellationRequested);
    }

    [Fact]
    public void Expire_Idempotent()
    {
        using var service = new GracefulShutdownService();

        service.Expire();
        service.Expire();
        service.Expire();

        Assert.True(service.Token.IsCancellationRequested);
    }

    [Fact]
    public void Expire_AfterDispose_DoesNotThrow()
    {
        var service = new GracefulShutdownService();
        service.Dispose();

        // Expire racing with dispose must not propagate to callers (signal handler /
        // watcher continuation contexts have nowhere meaningful to surface this).
        service.Expire();
    }

    [Fact]
    public void Token_RemainsAccessibleAfterDispose()
    {
        var service = new GracefulShutdownService();
        var token = service.Token;
        service.Dispose();

        // Token was captured up front; reading state after dispose must not throw.
        Assert.False(token.IsCancellationRequested);
    }
}
