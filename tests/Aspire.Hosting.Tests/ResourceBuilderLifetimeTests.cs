// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class ResourceBuilderLifetimeTests
{
    [Fact]
    public void WithLifetimeAddsContainerLifetimeAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var container = builder.AddContainer("container", "image")
            .WithLifetime(Lifetime.Persistent);

        var annotation = container.Resource.Annotations.OfType<ContainerLifetimeAnnotation>().Single();
        Assert.Equal(ContainerLifetime.Persistent, annotation.Lifetime);
    }

    [Fact]
    public void WithLifetimeRejectsUnsupportedResourceTypes()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var parameter = builder.AddParameter("parameter");

        void ConfigureLifetime() => parameter.WithLifetime(Lifetime.Persistent);

        var exception = Assert.Throws<InvalidOperationException>((Action)ConfigureLifetime);
        Assert.Contains("does not support lifetime configuration", exception.Message);
    }
}
