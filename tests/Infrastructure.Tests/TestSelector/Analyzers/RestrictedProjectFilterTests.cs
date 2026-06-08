// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using TestSelector.Analyzers;
using Xunit;

namespace Infrastructure.Tests.TestSelector.Analyzers;

public class RestrictedProjectFilterTests
{
    [Fact]
    public void Apply_EmptyRestrictedList_ReturnsAllProjectsUnchanged()
    {
        var input = new[] { "tests/A.Tests/A.Tests.csproj", "tests/B.Tests/B.Tests.csproj" };

        var result = RestrictedProjectFilter.Apply(input, Array.Empty<string>(), Array.Empty<string>());

        Assert.Equal(input, result);
    }

    [Fact]
    public void Apply_RestrictedProjectInMapped_KeepsIt()
    {
        // Mapping fired → restricted project is opt-in → keep it.
        var all = new[] { "tests/Foo.Tests/Foo.Tests.csproj", "tests/Restricted.Tests/Restricted.Tests.csproj" };
        var restricted = new[] { "tests/Restricted.Tests/Restricted.Tests.csproj" };
        var mapped = new[] { "tests/Restricted.Tests/Restricted.Tests.csproj" };

        var result = RestrictedProjectFilter.Apply(all, restricted, mapped);

        Assert.Contains("tests/Restricted.Tests/Restricted.Tests.csproj", result);
        Assert.Contains("tests/Foo.Tests/Foo.Tests.csproj", result);
    }

    [Fact]
    public void Apply_RestrictedProjectNotInMapped_DropsIt()
    {
        // Restricted project pulled in only by dotnet-affected transitive reference
        // (not by an explicit mapping hit) → drop it.
        var all = new[] { "tests/Foo.Tests/Foo.Tests.csproj", "tests/Restricted.Tests/Restricted.Tests.csproj" };
        var restricted = new[] { "tests/Restricted.Tests/Restricted.Tests.csproj" };
        var mapped = new[] { "tests/Foo.Tests/Foo.Tests.csproj" }; // only Foo, not Restricted

        var result = RestrictedProjectFilter.Apply(all, restricted, mapped);

        Assert.Contains("tests/Foo.Tests/Foo.Tests.csproj", result);
        Assert.DoesNotContain("tests/Restricted.Tests/Restricted.Tests.csproj", result);
    }

    [Fact]
    public void Apply_NormalizesSeparatorsAndCase()
    {
        // The rule comparison must tolerate Windows backslashes and case differences,
        // since paths can come from MSBuild on Windows and rules are author-supplied
        // strings.
        var all = new[] { "tests\\Restricted.Tests\\Restricted.Tests.csproj" };
        var restricted = new[] { "TESTS/restricted.tests/Restricted.Tests.csproj" };
        var mapped = Array.Empty<string>();

        var result = RestrictedProjectFilter.Apply(all, restricted, mapped);

        Assert.Empty(result);
    }

    [Fact]
    public void Apply_NonRestrictedProjects_PassThroughEvenWithoutMapping()
    {
        // Only restricted projects need the explicit opt-in. Regular projects keep their
        // existing behavior: any source of inclusion (mapping or dotnet-affected) keeps them.
        var all = new[] { "tests/Foo.Tests/Foo.Tests.csproj", "tests/Bar.Tests/Bar.Tests.csproj" };
        var restricted = new[] { "tests/Restricted.Tests/Restricted.Tests.csproj" };

        var result = RestrictedProjectFilter.Apply(all, restricted, Array.Empty<string>());

        Assert.Equal(all, result);
    }

    [Fact]
    public void Apply_MultipleRestrictedProjects_FiltersEachIndependently()
    {
        var all = new[]
        {
            "tests/A.Tests/A.Tests.csproj",         // not restricted, kept
            "tests/Acquisition.Tests/Acquisition.Tests.csproj",   // restricted, mapped → kept
            "tests/Infrastructure.Tests/Infrastructure.Tests.csproj", // restricted, not mapped → dropped
        };
        var restricted = new[]
        {
            "tests/Acquisition.Tests/Acquisition.Tests.csproj",
            "tests/Infrastructure.Tests/Infrastructure.Tests.csproj",
        };
        var mapped = new[] { "tests/Acquisition.Tests/Acquisition.Tests.csproj" };

        var result = RestrictedProjectFilter.Apply(all, restricted, mapped);

        Assert.Equal(
            new[]
            {
                "tests/A.Tests/A.Tests.csproj",
                "tests/Acquisition.Tests/Acquisition.Tests.csproj",
            },
            result);
    }
}
