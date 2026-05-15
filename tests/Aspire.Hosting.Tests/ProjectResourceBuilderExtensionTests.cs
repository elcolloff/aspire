// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class ProjectResourceBuilderExtensionTests
{
    [Fact]
    public void WithLifetimeAddsExecutableLifetimeAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var project = builder.AddProject<TestProject>("project", options => options.ExcludeLaunchProfile = true)
            .WithLifetime(Lifetime.Persistent);

        var annotation = project.Resource.Annotations.OfType<ExecutableLifetimeAnnotation>().Single();
        Assert.Equal(Lifetime.Persistent, annotation.Lifetime);
    }

    private sealed class TestProject : IProjectMetadata
    {
        public string ProjectPath => "test.csproj";
    }
}
