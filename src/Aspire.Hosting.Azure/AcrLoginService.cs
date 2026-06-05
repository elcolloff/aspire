// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECONTAINERRUNTIME001

using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Hosting.Publishing;
using Azure.Core;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Default implementation of <see cref="IAcrLoginService"/> that handles ACR authentication
/// using Azure credentials and OAuth2 token exchange.
/// </summary>
internal sealed class AcrLoginService : IAcrLoginService
{
    private const string AcrUsername = "00000000-0000-0000-0000-000000000000";
    private const string AcrScope = "https://containerregistry.azure.net/.default";
    private const int MaxExchangeAttempts = 4;

    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IContainerRuntimeResolver _containerRuntimeResolver;
    private readonly ILogger<AcrLoginService> _logger;

    private sealed class AcrRefreshTokenResponse
    {
        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int? ExpiresIn { get; set; }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AcrLoginService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory for making OAuth2 exchange requests.</param>
    /// <param name="containerRuntimeResolver">The container runtime resolver for performing registry login.</param>
    /// <param name="logger">The logger for diagnostic output.</param>
    public AcrLoginService(IHttpClientFactory httpClientFactory, IContainerRuntimeResolver containerRuntimeResolver, ILogger<AcrLoginService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _containerRuntimeResolver = containerRuntimeResolver ?? throw new ArgumentNullException(nameof(containerRuntimeResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task LoginAsync(
        string registryEndpoint,
        string tenantId,
        TokenCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryEndpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(credential);

        var refreshToken = await GetRefreshTokenAsync(registryEndpoint, tenantId, credential, cancellationToken).ConfigureAwait(false);

        var containerRuntime = await _containerRuntimeResolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
        await containerRuntime.LoginToRegistryAsync(registryEndpoint, refreshToken.Username, refreshToken.Token, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<AcrRefreshToken> GetRefreshTokenAsync(
        string registryEndpoint,
        string tenantId,
        TokenCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryEndpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(credential);

        var tokenRequestContext = new TokenRequestContext([AcrScope]);
        var aadToken = await credential.GetTokenAsync(tokenRequestContext, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("AAD access token acquired for ACR audience, registry: {RegistryEndpoint}, token length: {TokenLength}",
            registryEndpoint, aadToken.Token.Length);

        var refreshToken = await ExchangeAadTokenForAcrRefreshTokenAsync(
            registryEndpoint, tenantId, aadToken.Token, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("ACR refresh token acquired, length: {TokenLength}", refreshToken.Length);

        return new AcrRefreshToken(AcrUsername, refreshToken);
    }

    private async Task<string> ExchangeAadTokenForAcrRefreshTokenAsync(
        string registryEndpoint,
        string tenantId,
        string aadAccessToken,
        CancellationToken cancellationToken)
    {
        // Use named HTTP client "AcrLogin" which can be configured for debug-level logging
        // via configuration: "Logging": { "LogLevel": { "System.Net.Http.HttpClient.AcrLogin": "Debug" } }
        var httpClient = _httpClientFactory.CreateClient("AcrLogin");
        httpClient.Timeout = TimeSpan.FromSeconds(30);

        // ACR OAuth2 exchange endpoint
        var exchangeUrl = $"https://{registryEndpoint}/oauth2/exchange";

        _logger.LogDebug("Exchanging AAD token for ACR refresh token at {ExchangeUrl} (tenant: {TenantId})",
            exchangeUrl,
            tenantId);

        var formData = new Dictionary<string, string>
        {
            ["grant_type"] = "access_token",
            ["service"] = registryEndpoint,
            ["tenant"] = tenantId,
            ["access_token"] = aadAccessToken
        };

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var content = new FormUrlEncodedContent(formData);
                var response = await httpClient.PostAsync(exchangeUrl, content, cancellationToken).ConfigureAwait(false);

                // Read response body as string once
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var truncatedBody = responseBody.Length <= 1000 ? responseBody : responseBody[..1000] + "…";
                    throw new HttpRequestException(
                        $"POST /oauth2/exchange failed {(int)response.StatusCode} {response.ReasonPhrase}. Body: {truncatedBody}",
                        null,
                        response.StatusCode);
                }

                // Deserialize from the string we already read
                var tokenResponse = JsonSerializer.Deserialize<AcrRefreshTokenResponse>(responseBody, s_jsonOptions);

                if (string.IsNullOrEmpty(tokenResponse?.RefreshToken))
                {
                    throw new InvalidOperationException($"Response missing refresh_token.");
                }

                return tokenResponse.RefreshToken;
            }
            catch (HttpRequestException ex) when (IsTransientExchangeFailure(ex) && attempt < MaxExchangeAttempts)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                _logger.LogInformation(ex, "ACR refresh token exchange for registry {RegistryEndpoint} failed on attempt {Attempt}. Retrying in {DelaySeconds} second(s).",
                    registryEndpoint,
                    attempt,
                    delay.TotalSeconds);

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransientExchangeFailure(HttpRequestException exception)
    {
        return exception.StatusCode is null ||
            exception.StatusCode is HttpStatusCode.RequestTimeout or
                HttpStatusCode.TooManyRequests or
                HttpStatusCode.BadGateway or
                HttpStatusCode.ServiceUnavailable or
                HttpStatusCode.GatewayTimeout ||
            (int)exception.StatusCode >= 500;
    }
}
