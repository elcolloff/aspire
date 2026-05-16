// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// A filter applied to an HTTPRoute rule. Filters can mutate the request before it
/// reaches the backend, mutate the response on its way back, or short-circuit the
/// request entirely (for example, returning a redirect without ever invoking a backend).
/// </summary>
/// <remarks>
/// See <see href="https://gateway-api.sigs.k8s.io/api-types/httproute/#filters-optional"/>
/// for the full filter taxonomy. Today this type carries
/// <see cref="RequestRedirect"/> and <see cref="ResponseHeaderModifier"/>, the two
/// filters used by <see cref="KubernetesGatewayExtensions.WithTls(global::Aspire.Hosting.ApplicationModel.IResourceBuilder{KubernetesGatewayResource}, System.Action{TlsOptions}?)"/>
/// to implement the HTTP→HTTPS redirect and HSTS.
/// </remarks>
[YamlSerializable]
public sealed class HttpRouteFilterV1
{
    /// <summary>
    /// Gets or sets the filter type discriminator. Common values include
    /// <c>"RequestRedirect"</c>, <c>"ResponseHeaderModifier"</c>,
    /// <c>"RequestHeaderModifier"</c>, and <c>"URLRewrite"</c>.
    /// </summary>
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = null!;

    /// <summary>
    /// Gets or sets the <c>RequestRedirect</c> filter payload. Only meaningful when
    /// <see cref="Type"/> is <c>"RequestRedirect"</c>.
    /// </summary>
    [YamlMember(Alias = "requestRedirect")]
    public HttpRouteRequestRedirectV1? RequestRedirect { get; set; }

    /// <summary>
    /// Gets or sets the <c>ResponseHeaderModifier</c> filter payload. Only meaningful
    /// when <see cref="Type"/> is <c>"ResponseHeaderModifier"</c>.
    /// </summary>
    [YamlMember(Alias = "responseHeaderModifier")]
    public HttpRouteResponseHeaderModifierV1? ResponseHeaderModifier { get; set; }
}

/// <summary>
/// Payload for a <c>RequestRedirect</c> HTTPRoute filter. The Gateway returns a redirect
/// response to the client without forwarding the request to any backend.
/// </summary>
/// <remarks>
/// See <see href="https://gateway-api.sigs.k8s.io/api-types/httproute/#httprequestredirectfilter"/>.
/// </remarks>
[YamlSerializable]
public sealed class HttpRouteRequestRedirectV1
{
    /// <summary>
    /// Gets or sets the scheme used in the <c>Location</c> response header. Typically
    /// <c>"https"</c> for HTTP→HTTPS upgrades.
    /// </summary>
    [YamlMember(Alias = "scheme")]
    public string? Scheme { get; set; }

    /// <summary>
    /// Gets or sets the redirect status code. <c>301</c> matches the convention used by
    /// every major HTTPS-only site (GitHub, Microsoft, Stripe, Cloudflare, ...) and is
    /// the value <see cref="KubernetesGatewayExtensions.WithTls(global::Aspire.Hosting.ApplicationModel.IResourceBuilder{KubernetesGatewayResource}, System.Action{TlsOptions}?)"/>
    /// emits by default.
    /// </summary>
    [YamlMember(Alias = "statusCode")]
    public int? StatusCode { get; set; }
}

/// <summary>
/// Payload for a <c>ResponseHeaderModifier</c> HTTPRoute filter. The Gateway adds, sets,
/// or removes response headers before returning the response to the client.
/// </summary>
/// <remarks>
/// See <see href="https://gateway-api.sigs.k8s.io/api-types/httproute/#httpheaderfilter"/>.
/// Used by <see cref="KubernetesGatewayExtensions.WithTls(global::Aspire.Hosting.ApplicationModel.IResourceBuilder{KubernetesGatewayResource}, System.Action{TlsOptions}?)"/>
/// to inject <c>Strict-Transport-Security</c>.
/// </remarks>
[YamlSerializable]
public sealed class HttpRouteResponseHeaderModifierV1
{
    /// <summary>
    /// Gets the headers that must be set on the response, overwriting any existing value.
    /// </summary>
    [YamlMember(Alias = "set")]
    public List<HttpRouteHeaderV1> Set { get; } = [];

    /// <summary>
    /// Gets the headers that must be added to the response, preserving any existing value.
    /// </summary>
    [YamlMember(Alias = "add")]
    public List<HttpRouteHeaderV1> Add { get; } = [];

    /// <summary>
    /// Gets the names of headers that must be removed from the response.
    /// </summary>
    [YamlMember(Alias = "remove")]
    public List<string> Remove { get; } = [];
}

/// <summary>
/// A single header name/value pair used by header-modifier HTTPRoute filters.
/// </summary>
[YamlSerializable]
public sealed class HttpRouteHeaderV1
{
    /// <summary>
    /// Gets or sets the header name.
    /// </summary>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = null!;

    /// <summary>
    /// Gets or sets the header value.
    /// </summary>
    [YamlMember(Alias = "value")]
    public string Value { get; set; } = null!;
}
