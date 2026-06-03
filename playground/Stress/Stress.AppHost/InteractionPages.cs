// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only.

internal static class InteractionPages
{
    private const string LogoAssetRoute = "aspire-logo.svg";
    private const string TodoCssRoute = "todo-styles.css";

    // Shared todo state accessible by both the page and commands.
    private static readonly List<TodoItem> s_todos = new()
    {
        new(1, "Buy groceries"),
        new(2, "Write unit tests"),
        new(3, "Review pull request"),
        new(4, "Update documentation"),
        new(5, "Fix flaky test")
    };
    private static readonly object s_todosLock = new();
    private static readonly SemaphoreSlim s_todoChanged = new(0);
    private static int s_nextTodoId = 6;

    public static void Register(IServiceProvider services)
    {
        var interactionService = services.GetRequiredService<IInteractionService>();

        RegisterCounterPage(interactionService);
        RegisterMarkdownPage(interactionService);
        RegisterImageAssetPage(interactionService);
        RegisterTodoPage(interactionService);
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

                        [Echo Message](type=button command=echo-arguments resource=argument-commands arguments=message=Hello+from+button&repeat={count}&shout=true&flavor=vanilla icon=Send)
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

                    ![Aspire logo](/pages/assets/{LogoAssetRoute})
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

    private static void RegisterTodoPage(IInteractionService interactionService)
    {
        var todoCss = LoadEmbeddedTextResource("TodoStyles.css");
        interactionService.RegisterAsset(TodoCssRoute, "text/css", System.Text.Encoding.UTF8.GetBytes(todoCss));

        interactionService.RegisterPage("todo", new PageContext
        {
            Title = "Todo",
            CssRoutes = [TodoCssRoute],
            OnVisit = async visitContext =>
            {
                while (!visitContext.CancellationToken.IsCancellationRequested)
                {
                    var markdown = BuildTodoMarkdown();
                    await visitContext.SendMarkdownAsync(markdown, visitContext.CancellationToken);

                    // Wait for a change notification or timeout to poll periodically.
                    await s_todoChanged.WaitAsync(visitContext.CancellationToken);
                }
            }
        });

        interactionService.RegisterMenuButton(new MenuButtonOptions
        {
            IconName = "ClipboardTaskList",
            Text = "Todo",
            Tooltip = "View the todo list page",
            Url = "/pages/todo"
        });
    }

    /// <summary>
    /// Registers todo commands on a command group resource.
    /// Called at build time from AppHost.cs.
    /// </summary>
    public static void AddTodoCommands(IDistributedApplicationBuilder builder, IResourceBuilder<ProjectResource> parentResource)
    {
        var todoCommands = builder.AddCommandGroup("todo-commands", parentResource.Resource);
        todoCommands.WithCommand(
            name: "add-todo",
            displayName: "Add todo",
            executeCommand: c =>
            {
                var title = c.Arguments.GetString("title");
                if (string.IsNullOrWhiteSpace(title))
                {
                    return Task.FromResult(CommandResults.Failure("The title argument is required."));
                }

                lock (s_todosLock)
                {
                    s_todos.Add(new TodoItem(s_nextTodoId++, title));
                }
                NotifyTodoChanged();

                return Task.FromResult(CommandResults.Success("Todo added."));
            },
            commandOptions: new CommandOptions
            {
                Description = "Add a new todo item to the list.",
                IconName = "Add",
                Arguments =
                [
                    new InteractionInput
                    {
                        Name = "title",
                        Label = "Title",
                        InputType = InputType.Text,
                        Required = true,
                        Placeholder = "What needs to be done?"
                    }
                ]
            });
        todoCommands.WithCommand(
            name: "delete-todo",
            displayName: "Delete todo",
            executeCommand: c =>
            {
                var idString = c.Arguments.GetString("id");
                if (!int.TryParse(idString, out var id))
                {
                    return Task.FromResult(CommandResults.Failure("The id argument is required."));
                }

                bool removed;
                lock (s_todosLock)
                {
                    removed = s_todos.RemoveAll(t => t.Id == id) > 0;
                }

                if (!removed)
                {
                    return Task.FromResult(CommandResults.Failure($"Todo with id {id} not found."));
                }

                NotifyTodoChanged();
                return Task.FromResult(CommandResults.Success("Todo deleted."));
            },
            commandOptions: new CommandOptions
            {
                Description = "Delete a todo item by id.",
                IconName = "Delete",
                Arguments =
                [
                    new InteractionInput
                    {
                        Name = "id",
                        Label = "Id",
                        InputType = InputType.Number,
                        Required = true
                    }
                ]
            });
    }

    private static string BuildTodoMarkdown()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Todo List");
        sb.AppendLine();

        sb.AppendLine("[Add Todo](type=button command=add-todo resource=todo-commands icon=Add)");
        sb.AppendLine();

        List<TodoItem> snapshot;
        lock (s_todosLock)
        {
            snapshot = new List<TodoItem>(s_todos);
        }

        if (snapshot.Count == 0)
        {
            sb.AppendLine("🚀 You're all caught up! No todos remaining.");
        }
        else
        {
            sb.AppendLine("| Todo | |");
            sb.AppendLine("|------|---|");
            foreach (var todo in snapshot)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {todo.Title} | [](type=button command=delete-todo resource=todo-commands arguments=id={todo.Id} icon=Delete) |");
            }
        }

        return sb.ToString();
    }

    private static void NotifyTodoChanged()
    {
        // Release all waiting visitors so they re-render.
        // CurrentCount can go negative with Release, but we just want to unblock waiters.
        s_todoChanged.Release();
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

    private sealed record TodoItem(int Id, string Title);
}
