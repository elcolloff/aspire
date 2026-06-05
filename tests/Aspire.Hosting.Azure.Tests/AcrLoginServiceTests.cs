// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECONTAINERRUNTIME001

using System.Net;
using Aspire.Hosting.Publishing;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Azure.Tests;

public class AcrLoginServiceTests
{
    [Fact]
    public async Task GetRefreshTokenRetriesTransientExchangeFailures()
    {
        var attempts = 0;
        using var httpClient = new HttpClient(new RecordingHandler(_ =>
        {
            attempts++;

            return attempts == 1
                ? throw new HttpRequestException("Transient socket failure")
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"refresh_token":"refresh-token","expires_in":3600}""")
                };
        }));

        var service = new AcrLoginService(
            new TestHttpClientFactory(httpClient),
            new ThrowingContainerRuntimeResolver(),
            NullLogger<AcrLoginService>.Instance);

        var token = await service.GetRefreshTokenAsync(
            "registry.azurecr.io",
            "tenant",
            new TestTokenCredential());

        Assert.Equal("00000000-0000-0000-0000-000000000000", token.Username);
        Assert.Equal("refresh-token", token.Token);
        Assert.Equal(2, attempts);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }

    private sealed class TestHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class TestTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            return new AccessToken("aad-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(GetToken(requestContext, cancellationToken));
        }
    }

    private sealed class ThrowingContainerRuntimeResolver : IContainerRuntimeResolver
    {
        public Task<IContainerRuntime> ResolveAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
