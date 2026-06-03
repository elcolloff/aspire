// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;

namespace Aspire.Dashboard.Model.Interaction;

/// <summary>
/// Tracks active menu buttons and page content updates from the AppHost interaction service.
/// </summary>
public sealed class CustomInteractionState
{
    private readonly object _lock = new();
    private ImmutableArray<MenuButtonState> _menuButtons = [];

    public event Action? OnMenuButtonsChanged;
    public event Action<PageContentUpdate>? OnPageContentUpdated;

    public ImmutableArray<MenuButtonState> MenuButtons
    {
        get
        {
            lock (_lock)
            {
                return _menuButtons;
            }
        }
    }

    public void AddMenuButton(int interactionId, string iconName, string text, string tooltip, string url)
    {
        lock (_lock)
        {
            // Idempotent — don't add if already registered (e.g. on reconnection).
            if (_menuButtons.Any(b => b.InteractionId == interactionId))
            {
                return;
            }
            _menuButtons = _menuButtons.Add(new MenuButtonState(interactionId, iconName, text, tooltip, url));
        }
        OnMenuButtonsChanged?.Invoke();
    }

    public void RemoveMenuButton(int interactionId)
    {
        lock (_lock)
        {
            _menuButtons = _menuButtons.RemoveAll(b => b.InteractionId == interactionId);
        }
        OnMenuButtonsChanged?.Invoke();
    }

    public void UpdatePageContent(int interactionId, string route, string sessionId, string markdownContent, string title, IReadOnlyList<string> styleIncludes, IReadOnlyList<string> scriptIncludes, bool enableHtml)
    {
        OnPageContentUpdated?.Invoke(new PageContentUpdate(interactionId, route, sessionId, markdownContent, title, styleIncludes, scriptIncludes, enableHtml));
    }
}

public sealed record MenuButtonState(int InteractionId, string IconName, string Text, string Tooltip, string Url);

public sealed record PageContentUpdate(int InteractionId, string Route, string SessionId, string MarkdownContent, string Title, IReadOnlyList<string> StyleIncludes, IReadOnlyList<string> ScriptIncludes, bool EnableHtml);
