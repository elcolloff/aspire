// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for installing cert-manager into a Kubernetes environment
/// and declaring <c>ClusterIssuer</c> resources against it.
/// </summary>
public static class CertManagerExtensions
{
    // The pinned default cert-manager chart version. Bump deliberately when validating against
    // a newer release; the Helm chart's API and CRDs evolve across minor versions.
    private const string DefaultChartReference = "oci://quay.io/jetstack/charts/cert-manager";
    private const string DefaultChartVersion = "v1.18.2";

    // Well-known ACME directory endpoints. See https://letsencrypt.org/docs/acme-protocol-updates/
    // for the current canonical URLs.
    private const string LetsEncryptProductionUrl = "https://acme-v02.api.letsencrypt.org/directory";
    private const string LetsEncryptStagingUrl = "https://acme-staging-v02.api.letsencrypt.org/directory";

    // The annotation cert-manager watches on Gateway / Ingress resources to auto-provision
    // a Certificate from the named ClusterIssuer.
    // See https://cert-manager.io/docs/usage/gateway/ and
    // https://cert-manager.io/docs/usage/ingress/.
    internal const string ClusterIssuerAnnotationKey = "cert-manager.io/cluster-issuer";

    /// <summary>
    /// Installs cert-manager into the Kubernetes environment and returns a typed
    /// <see cref="CertManagerResource"/> that can host issuer resources.
    /// </summary>
    /// <param name="builder">The Kubernetes environment resource builder.</param>
    /// <param name="name">The Aspire resource name for the cert-manager installation. Defaults to <c>"cert-manager"</c>.</param>
    /// <param name="chartVersion">The cert-manager Helm chart version to install.
    /// Defaults to a pinned version validated against this Aspire build.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{CertManagerResource}"/> for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Internally creates a <see cref="KubernetesHelmChartResource"/> via
    /// <see cref="KubernetesHelmChartExtensions.AddHelmChart(IResourceBuilder{KubernetesEnvironmentResource}, string, string, string)"/>
    /// pointed at <c>oci://quay.io/jetstack/charts/cert-manager</c>. The chart is configured with:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>crds.enabled = true</c> — installs the cert-manager CRDs (<c>ClusterIssuer</c>, <c>Certificate</c>, ...) so issuer manifests can be applied immediately afterwards.</item>
    ///   <item><c>config.enableGatewayAPI = true</c> — lets cert-manager watch Gateway API <c>Gateway</c>/<c>HTTPRoute</c> resources for the cluster-issuer annotation.</item>
    ///   <item><c>WithForceConflicts()</c> — works around the AKS Azure Policy add-on mutating cert-manager's <c>ValidatingWebhookConfiguration</c> after install.</item>
    ///   <item><c>WithDestroy()</c> — cleans up the Helm release on <c>aspire destroy</c>.</item>
    /// </list>
    /// <para>
    /// To customise additional Helm values, access the underlying chart via
    /// <see cref="CertManagerResource.HelmChart"/>.
    /// </para>
    /// </remarks>
    [AspireExport(Description = "Installs cert-manager into a Kubernetes environment")]
    public static IResourceBuilder<CertManagerResource> AddCertManager(
        this IResourceBuilder<KubernetesEnvironmentResource> builder,
        [ResourceName] string name = "cert-manager",
        string? chartVersion = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var version = chartVersion ?? DefaultChartVersion;

        var chartBuilder = builder
            .AddHelmChart(name, DefaultChartReference, version)
            .WithHelmValue("crds.enabled", "true")
            // Gateway API support is opt-in in the cert-manager chart. Without these values
            // cert-manager will not provision Certificates for Gateway listeners.
            // See https://cert-manager.io/docs/usage/gateway/.
            .WithHelmValue("config.apiVersion", "controller.config.cert-manager.io/v1alpha1")
            .WithHelmValue("config.kind", "ControllerConfiguration")
            .WithHelmValue("config.enableGatewayAPI", "true")
            .WithForceConflicts()
            .WithDestroy();

        var resource = new CertManagerResource(name, builder.Resource, chartBuilder.Resource);

        // CertManagerResource is a typed handle around the underlying KubernetesHelmChartResource:
        // the chart is what actually gets deployed (it's already in the model via AddHelmChart
        // above), so we deliberately don't AddResource() here. That keeps the user-visible
        // resource name (the chart) unique and avoids a name collision with the wrapper.
        return builder.ApplicationBuilder.CreateResourceBuilder(resource);
    }

    /// <summary>
    /// Adds a cert-manager <c>ClusterIssuer</c> to this cert-manager installation.
    /// </summary>
    /// <param name="builder">The cert-manager resource builder.</param>
    /// <param name="name">The Aspire resource name. Also used as the <c>metadata.name</c>
    /// of the generated <c>ClusterIssuer</c>, so it must be a valid DNS-1123 label.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{CertManagerIssuerResource}"/> for chaining.</returns>
    [AspireExport(Description = "Adds a cert-manager ClusterIssuer")]
    public static IResourceBuilder<CertManagerIssuerResource> AddIssuer(
        this IResourceBuilder<CertManagerResource> builder,
        [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var issuer = new CertManagerIssuerResource(name, builder.Resource);
        builder.Resource.Issuers.Add(issuer);

        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            return builder.ApplicationBuilder.CreateResourceBuilder(issuer);
        }

        return builder.ApplicationBuilder.AddResource(issuer).ExcludeFromManifest();
    }

    /// <summary>
    /// Configures the issuer to use the Let's Encrypt production ACME endpoint.
    /// </summary>
    /// <param name="builder">The issuer resource builder.</param>
    /// <param name="email">The contact email registered with the ACME account. Let's Encrypt
    /// uses this address for expiry notifications and rate-limit appeals.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{CertManagerIssuerResource}"/> for chaining.</returns>
    /// <remarks>
    /// Production certificates are subject to strict per-domain rate limits
    /// (<see href="https://letsencrypt.org/docs/rate-limits/"/>). For development workflows,
    /// prefer <see cref="WithLetsEncryptStaging(IResourceBuilder{CertManagerIssuerResource}, string)"/>
    /// which uses untrusted staging certificates with much higher rate limits.
    /// </remarks>
    [AspireExport(Description = "Configures the issuer for Let's Encrypt production")]
    public static IResourceBuilder<CertManagerIssuerResource> WithLetsEncryptProduction(
        this IResourceBuilder<CertManagerIssuerResource> builder,
        string email)
        => WithAcmeServer(builder, LetsEncryptProductionUrl, email);

    /// <summary>
    /// Configures the issuer to use the Let's Encrypt production ACME endpoint, with the
    /// contact email supplied via a parameter resolved at deploy time.
    /// </summary>
    /// <param name="builder">The issuer resource builder.</param>
    /// <param name="email">A parameter resource builder whose value is the contact email.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{CertManagerIssuerResource}"/> for chaining.</returns>
    [AspireExport("withLetsEncryptProductionParam", Description = "Configures the issuer for Let's Encrypt production with a parameterized email")]
    public static IResourceBuilder<CertManagerIssuerResource> WithLetsEncryptProduction(
        this IResourceBuilder<CertManagerIssuerResource> builder,
        IResourceBuilder<ParameterResource> email)
        => WithAcmeServer(builder, LetsEncryptProductionUrl, email);

    /// <summary>
    /// Configures the issuer to use the Let's Encrypt staging ACME endpoint. Certificates issued
    /// from staging are not trusted by browsers, but the endpoint has much higher rate limits,
    /// making it the right choice for development and CI workflows.
    /// </summary>
    /// <param name="builder">The issuer resource builder.</param>
    /// <param name="email">The contact email registered with the ACME account.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{CertManagerIssuerResource}"/> for chaining.</returns>
    [AspireExport(Description = "Configures the issuer for Let's Encrypt staging")]
    public static IResourceBuilder<CertManagerIssuerResource> WithLetsEncryptStaging(
        this IResourceBuilder<CertManagerIssuerResource> builder,
        string email)
        => WithAcmeServer(builder, LetsEncryptStagingUrl, email);

    /// <summary>
    /// Configures the issuer to use the Let's Encrypt staging ACME endpoint, with the
    /// contact email supplied via a parameter.
    /// </summary>
    [AspireExport("withLetsEncryptStagingParam", Description = "Configures the issuer for Let's Encrypt staging with a parameterized email")]
    public static IResourceBuilder<CertManagerIssuerResource> WithLetsEncryptStaging(
        this IResourceBuilder<CertManagerIssuerResource> builder,
        IResourceBuilder<ParameterResource> email)
        => WithAcmeServer(builder, LetsEncryptStagingUrl, email);

    /// <summary>
    /// Configures the issuer to use a custom ACME directory endpoint (e.g., a private ACME
    /// server such as ZeroSSL or step-ca).
    /// </summary>
    /// <param name="builder">The issuer resource builder.</param>
    /// <param name="serverUrl">The ACME directory URL (e.g., <c>https://acme.example.com/directory</c>).</param>
    /// <param name="email">The contact email registered with the ACME account.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{CertManagerIssuerResource}"/> for chaining.</returns>
    [AspireExport(Description = "Configures the issuer to use a custom ACME directory")]
    public static IResourceBuilder<CertManagerIssuerResource> WithAcmeServer(
        this IResourceBuilder<CertManagerIssuerResource> builder,
        string serverUrl,
        string email)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(serverUrl);
        ArgumentException.ThrowIfNullOrEmpty(email);

        builder.Resource.Spec = new CertManagerAcmeIssuerSpec(
            ReferenceExpression.Create($"{serverUrl}"),
            ReferenceExpression.Create($"{email}"));

        return builder;
    }

    /// <summary>
    /// Configures the issuer to use a custom ACME directory endpoint with a parameterized email.
    /// </summary>
    [AspireExport("withAcmeServerParam", Description = "Configures the issuer to use a custom ACME directory with a parameterized email")]
    public static IResourceBuilder<CertManagerIssuerResource> WithAcmeServer(
        this IResourceBuilder<CertManagerIssuerResource> builder,
        string serverUrl,
        IResourceBuilder<ParameterResource> email)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(serverUrl);
        ArgumentNullException.ThrowIfNull(email);

        builder.Resource.Spec = new CertManagerAcmeIssuerSpec(
            ReferenceExpression.Create($"{serverUrl}"),
            ReferenceExpression.Create($"{email.Resource}"));

        return builder;
    }

    /// <summary>
    /// Adds an HTTP-01 ACME challenge solver to the issuer. cert-manager will satisfy the
    /// challenge by provisioning a temporary HTTP route at
    /// <c>/.well-known/acme-challenge/{token}</c> on the same hostname being validated.
    /// This requires the hostname to be publicly reachable on port 80.
    /// </summary>
    /// <param name="builder">The issuer resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{CertManagerIssuerResource}"/> for chaining.</returns>
    /// <remarks>
    /// HTTP-01 is the right choice for gateways exposed via Azure Application Gateway for
    /// Containers (AGC) or any ingress controller that publishes a publicly addressable
    /// hostname. Wildcard certificates require a DNS-01 solver, which is not yet supported.
    /// </remarks>
    [AspireExport(Description = "Adds an HTTP-01 ACME challenge solver to the issuer")]
    public static IResourceBuilder<CertManagerIssuerResource> WithHttp01Solver(
        this IResourceBuilder<CertManagerIssuerResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.Solvers.Add(new CertManagerHttp01SolverConfig());
        return builder;
    }

    /// <summary>
    /// Adds an HTTPS listener to the gateway and wires it to the supplied cert-manager
    /// <c>ClusterIssuer</c>. This adds the <c>cert-manager.io/cluster-issuer</c> annotation
    /// to the generated Gateway resource, causing cert-manager to provision and renew a
    /// certificate for each gateway listener hostname.
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="issuer">The cert-manager <c>ClusterIssuer</c> to issue certificates from.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    /// <remarks>
    /// Equivalent to calling <c>WithTls()</c> followed by
    /// <c>WithGatewayAnnotation("cert-manager.io/cluster-issuer", issuer.Resource.Name)</c>,
    /// but type-safe and refactor-friendly.
    /// </remarks>
    [AspireExport("withGatewayTlsIssuer", Description = "Configures TLS on a Kubernetes Gateway using a cert-manager ClusterIssuer")]
    public static IResourceBuilder<KubernetesGatewayResource> WithTls(
        this IResourceBuilder<KubernetesGatewayResource> builder,
        IResourceBuilder<CertManagerIssuerResource> issuer)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(issuer);

        return builder
            .WithTls()
            .WithGatewayAnnotation(ClusterIssuerAnnotationKey, issuer.Resource.Name);
    }

    /// <summary>
    /// Adds TLS configuration to the ingress and wires it to the supplied cert-manager
    /// <c>ClusterIssuer</c>. This adds the <c>cert-manager.io/cluster-issuer</c> annotation
    /// to the generated Ingress resource, causing cert-manager to provision and renew a
    /// certificate for each ingress host.
    /// </summary>
    /// <param name="builder">The ingress resource builder.</param>
    /// <param name="issuer">The cert-manager <c>ClusterIssuer</c> to issue certificates from.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesIngressResource}"/> for chaining.</returns>
    [AspireExport("withIngressTlsIssuer", Description = "Configures TLS on a Kubernetes Ingress using a cert-manager ClusterIssuer")]
    public static IResourceBuilder<KubernetesIngressResource> WithTls(
        this IResourceBuilder<KubernetesIngressResource> builder,
        IResourceBuilder<CertManagerIssuerResource> issuer)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(issuer);

        return builder
            .WithTls()
            .WithIngressAnnotation(ClusterIssuerAnnotationKey, issuer.Resource.Name);
    }
}
