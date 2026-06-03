// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only.

internal sealed class TodoInteraction
{
    private const string TodoCssRoute = "todo-styles.css";

    // Shared todo state accessible by all page visits.
    private readonly List<TodoItem> _todos = new()
    {
        new(1, "Buy groceries"),
        new(2, "Write unit tests"),
        new(3, "Review pull request"),
        new(4, "Update documentation"),
        new(5, "Fix flaky test")
    };

    private readonly object _todosLock = new();
    private readonly SemaphoreSlim _todoChanged = new(0);
    private int _nextTodoId = 6;

    public TodoInteraction(IResourceBuilder<ProjectResource> parentResource)
    {
        ArgumentNullException.ThrowIfNull(parentResource);
    }

    public void Register(IDistributedApplicationBuilder builder)
    {
        builder.OnBeforeStart((@event, ct) =>
        {
            var interactionService = @event.Services.GetRequiredService<IInteractionService>();
            RegisterPage(interactionService);

            return Task.CompletedTask;
        });
    }

    private void RegisterPage(IInteractionService interactionService)
    {
        var todoCss = LoadEmbeddedTextResource("TodoStyles.css");
        interactionService.RegisterAsset(TodoCssRoute, "text/css", Encoding.UTF8.GetBytes(todoCss));

        interactionService.RegisterPage("todo", new PageContext
        {
            Title = "Todo",
            StyleIncludes = [TodoCssRoute],
            Actions = new Dictionary<string, Func<ActionContext, Task>>(StringComparer.OrdinalIgnoreCase)
            {
                ["add-todo"] = context => AddTodoAsync(interactionService, context),
                ["delete-todo"] = DeleteTodoAsync
            },
            OnVisit = async visitContext =>
            {
                while (!visitContext.CancellationToken.IsCancellationRequested)
                {
                    var markdown = BuildTodoMarkdown();
                    await visitContext.SendMarkdownAsync(markdown, visitContext.CancellationToken);

                    // Wait for a change notification or timeout to poll periodically.
                    await _todoChanged.WaitAsync(visitContext.CancellationToken);
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

    private async Task AddTodoAsync(IInteractionService interactionService, ActionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();

        var result = await interactionService.PromptInputAsync(
            "Add todo",
            "Add a new todo item to the list.",
            new InteractionInput
            {
                Name = "title",
                Label = "Title",
                InputType = InputType.Text,
                Required = true,
                Placeholder = "What needs to be done?"
            },
            cancellationToken: context.CancellationToken);

        if (result.Canceled || string.IsNullOrWhiteSpace(result.Data.Value))
        {
            return;
        }

        lock (_todosLock)
        {
            _todos.Add(new TodoItem(_nextTodoId++, result.Data.Value.Trim()));
        }

        NotifyTodoChanged();
    }

    private Task DeleteTodoAsync(ActionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();

        if (!int.TryParse(context.Arguments.GetValueOrDefault("id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            return Task.CompletedTask;
        }

        bool removed;
        lock (_todosLock)
        {
            removed = _todos.RemoveAll(t => t.Id == id) > 0;
        }

        if (removed)
        {
            NotifyTodoChanged();
        }

        return Task.CompletedTask;
    }

    private string BuildTodoMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Todo List");
        sb.AppendLine();

        sb.AppendLine("[Add Todo](type=button action=add-todo icon=Add)");
        sb.AppendLine();

        List<TodoItem> snapshot;
        lock (_todosLock)
        {
            snapshot = new List<TodoItem>(_todos);
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
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {EscapeMarkdownTableCell(todo.Title)} | [](type=button action=delete-todo arguments=id={todo.Id} icon=Delete) |");
            }
        }

        return sb.ToString();
    }

    private static string EscapeMarkdownTableCell(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal)
            .Replace("~", "\\~", StringComparison.Ordinal);
    }

    private void NotifyTodoChanged()
    {
        // Release all waiting visitors so they re-render.
        // CurrentCount can go negative with Release, but we just want to unblock waiters.
        _todoChanged.Release();
    }

    private static string LoadEmbeddedTextResource(string fileName)
    {
        using var stream = OpenEmbeddedResource(fileName);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static Stream OpenEmbeddedResource(string fileName)
    {
        var resourceName = $"Stress.AppHost.Resources.{fileName}";
        return Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"{fileName} embedded resource not found.");
    }

    private sealed record TodoItem(int Id, string Title);
}