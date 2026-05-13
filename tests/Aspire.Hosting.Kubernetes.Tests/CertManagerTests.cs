// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Kubernetes.Tests;

public class CertManagerTests
{
    [Fact]
    public void AddCertManager_RegistersWrapperAndHelmChartUnderSeparateNames()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");

        var certManager = k8s.AddCertManager();

        Assert.Equal("cert-manager", certManager.Resource.Name);
        Assert.Same(k8s.Resource, certManager.Resource.Parent);

        // The wrapper keeps the natural "{name}" identifier; the helm chart is registered
        // under "{name}-chart" so both can coexist in the model without colliding.
        Assert.Equal("cert-manager-chart", certManager.Resource.HelmChart.Name);
        Assert.Equal("oci://quay.io/jetstack/charts/cert-manager", certManager.Resource.HelmChart.ChartReference);
        Assert.StartsWith("v", certManager.Resource.HelmChart.ChartVersion);

        // CRDs and Gateway API support are required for cert-manager to issue certificates
        // for Aspire-modeled Gateway resources, so they must be on by default.
        Assert.Equal("true", certManager.Resource.HelmChart.Values["crds.enabled"]);
        Assert.Equal("true", certManager.Resource.HelmChart.Values["config.enableGatewayAPI"]);

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        Assert.Contains(appModel.Resources, r => r is CertManagerResource c && c.Name == "cert-manager");
        Assert.Contains(appModel.Resources, r => r is KubernetesHelmChartResource c && c.Name == "cert-manager-chart");
    }

    [Fact]
    public void AddIssuer_WithLetsEncryptProductionAndHttp01_PopulatesSpecAndSolver()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");
        var certManager = k8s.AddCertManager();

        var issuer = certManager.AddIssuer("letsencrypt-prod")
            .WithLetsEncryptProduction("ops@contoso.com")
            .WithHttp01Solver();

        Assert.Equal("letsencrypt-prod", issuer.Resource.Name);
        Assert.Same(certManager.Resource, issuer.Resource.Parent);
        Assert.Contains(issuer.Resource, certManager.Resource.Issuers);

        var spec = Assert.IsType<CertManagerAcmeIssuerSpec>(issuer.Resource.Spec);
        Assert.Equal("https://acme-v02.api.letsencrypt.org/directory", spec.ServerUrl.Format);
        Assert.Equal("ops@contoso.com", spec.Email.Format);

        var solver = Assert.Single(issuer.Resource.Solvers);
        Assert.IsType<CertManagerHttp01SolverConfig>(solver);
    }

    [Fact]
    public void WithLetsEncryptStaging_UsesStagingDirectoryUrl()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var certManager = builder.AddKubernetesEnvironment("env").AddCertManager();

        var issuer = certManager.AddIssuer("le-staging")
            .WithLetsEncryptStaging("ops@contoso.com");

        var spec = Assert.IsType<CertManagerAcmeIssuerSpec>(issuer.Resource.Spec);
        Assert.Equal("https://acme-staging-v02.api.letsencrypt.org/directory", spec.ServerUrl.Format);
    }

    [Fact]
    public void WithAcmeServer_AllowsCustomDirectoryUrl()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var certManager = builder.AddKubernetesEnvironment("env").AddCertManager();

        var issuer = certManager.AddIssuer("custom-acme")
            .WithAcmeServer("https://acme.example.com/directory", "ops@contoso.com");

        var spec = Assert.IsType<CertManagerAcmeIssuerSpec>(issuer.Resource.Spec);
        Assert.Equal("https://acme.example.com/directory", spec.ServerUrl.Format);
    }

    [Fact]
    public void Gateway_WithTls_Issuer_AddsClusterIssuerAnnotation()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");
        var issuer = k8s.AddCertManager()
            .AddIssuer("letsencrypt-prod")
            .WithLetsEncryptProduction("ops@contoso.com")
            .WithHttp01Solver();

        var gateway = k8s.AddGateway("public")
            .WithGatewayClass("nginx")
            .WithTls(issuer);

        // The typed WithTls(issuer) overload should be equivalent to WithTls() + setting
        // the cert-manager.io/cluster-issuer annotation to the issuer's name.
        Assert.Single(gateway.Resource.TlsConfigs);
        Assert.True(gateway.Resource.GatewayAnnotations.TryGetValue(
            CertManagerExtensions.ClusterIssuerAnnotationKey, out var value));
        Assert.Equal("letsencrypt-prod", value!.Format);
    }

    [Fact]
    public void Ingress_WithTls_Issuer_AddsClusterIssuerAnnotation()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");
        var issuer = k8s.AddCertManager()
            .AddIssuer("letsencrypt-prod")
            .WithLetsEncryptProduction("ops@contoso.com")
            .WithHttp01Solver();

        var ingress = k8s.AddIngress("public")
            .WithIngressClass("nginx")
            .WithTls(issuer);

        Assert.Single(ingress.Resource.TlsConfigs);
        Assert.True(ingress.Resource.IngressAnnotations.TryGetValue(
            CertManagerExtensions.ClusterIssuerAnnotationKey, out var value));
        Assert.Equal("letsencrypt-prod", value!.Format);
    }

    [Fact]
    public void AddCertManager_RunMode_DoesNotRegisterResources()
    {
        // In run mode neither the wrapper nor its helm chart get added to the model
        // (helm install only runs at deploy time, and AddHelmChart already suppresses
        // its resource in run mode).
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var k8s = builder.AddKubernetesEnvironment("env");

        var certManager = k8s.AddCertManager();
        Assert.NotNull(certManager.Resource);

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        Assert.DoesNotContain(appModel.Resources, r => r is CertManagerResource);
    }
}
