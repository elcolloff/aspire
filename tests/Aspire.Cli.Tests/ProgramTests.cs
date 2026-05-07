// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests;

public class ProgramTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void CaptureParsedCommand_UsesRootCommandForVersionOption()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var rootCommand = provider.GetRequiredService<RootCommand>();
        var executionContext = provider.GetRequiredService<CliExecutionContext>();
        var parseResult = rootCommand.Parse(["--version"]);

        Program.CaptureParsedCommand(rootCommand, parseResult, executionContext);

        Assert.Same(rootCommand, executionContext.Command);
        Assert.True(Program.IsVersionOptionRequested(rootCommand, parseResult));
    }

    [Fact]
    public void IsVersionOptionRequested_IgnoresSubcommandVersionOption()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var rootCommand = provider.GetRequiredService<RootCommand>();
        var parseResult = rootCommand.Parse(["add", "Aspire.Hosting.Redis", "--version", "9.4.0"]);

        Assert.False(Program.IsVersionOptionRequested(rootCommand, parseResult));
    }
}
