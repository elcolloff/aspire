// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only.

internal static class InteractionPages
{
    private const string LogoAssetRoute = "aspire-logo.svg";

    public static void Register(IServiceProvider services)
    {
        var interactionService = services.GetRequiredService<IInteractionService>();

        RegisterCounterPage(interactionService);
        RegisterMarkdownPage(interactionService);
        RegisterImageAssetPage(interactionService);
    }

    private static void RegisterCounterPage(IInteractionService interactionService)
    {
        interactionService.RegisterPage("counter", new PageContext
        {
            Title = "Counter",
            OnVisit = async visitContext =>
            {
                var logger = visitContext.Services.GetRequiredService<ILoggerFactory>().CreateLogger("CounterPage");

                visitContext.CancellationToken.Register(() => logger.LogInformation("Visitor {SessionId} left the counter page.", visitContext.SessionId));

                var count = 0;
                var resets = 0;
                while (!visitContext.CancellationToken.IsCancellationRequested)
                {
                    count++;
                    if (count > 10)
                    {
                        count = 1;
                        resets++;
                    }

                    await visitContext.SendMarkdownAsync(
                        $"""
                        # Counter

                        Current count: **{count}**

                        Resets: **{resets}**

                        Updates every second.
                        """, visitContext.CancellationToken);
                    await Task.Delay(1000, visitContext.CancellationToken);
                }
            }
        });

        interactionService.RegisterMenuButton(new MenuButtonOptions
        {
            IconName = "NumberSymbol",
            Text = "Counter",
            Tooltip = "View the live counter page",
            Url = "/pages/counter"
        });
    }

    private static void RegisterMarkdownPage(IInteractionService interactionService)
    {
        var markdownShowcase = LoadEmbeddedTextResource("MarkdownShowcase.txt");

        interactionService.RegisterPage("markdown", new PageContext
        {
            Title = "Markdown Showcase",
            OnVisit = async visitContext =>
            {
                await visitContext.SendMarkdownAsync(markdownShowcase, visitContext.CancellationToken);
            }
        });

        interactionService.RegisterMenuButton(new MenuButtonOptions
        {
            IconName = "Document",
            Text = "Markdown",
            Tooltip = "View the markdown showcase page",
            Url = "/pages/markdown"
        });
    }

    private static void RegisterImageAssetPage(IInteractionService interactionService)
    {
        var logoBytes = LoadEmbeddedBinaryResource("AspireLogo.svg");
        interactionService.RegisterAsset(LogoAssetRoute, "image/svg+xml", logoBytes);

        interactionService.RegisterPage("image-asset", new PageContext
        {
            Title = "Image Asset",
            OnVisit = async visitContext =>
            {
                await visitContext.SendMarkdownAsync(
                    $"""
                    # Image Asset

                    This image is served from a globally registered embedded resource asset.

                    ![Aspire logo](/assets/{LogoAssetRoute})
                    """, visitContext.CancellationToken);
            }
        });

        interactionService.RegisterMenuButton(new MenuButtonOptions
        {
            IconName = "Image",
            Text = "Image Asset",
            Tooltip = "View an image served from embedded resources",
            Url = "/pages/image-asset"
        });
    }

    private static string LoadEmbeddedTextResource(string fileName)
    {
        using var stream = OpenEmbeddedResource(fileName);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static byte[] LoadEmbeddedBinaryResource(string fileName)
    {
        using var stream = OpenEmbeddedResource(fileName);
        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }

    private static Stream OpenEmbeddedResource(string fileName)
    {
        var resourceName = $"Stress.AppHost.Resources.{fileName}";
        return Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"{fileName} embedded resource not found.");
    }
}
