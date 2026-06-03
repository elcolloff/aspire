// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;

namespace Aspire.Dashboard.Model.Interaction;

/// <summary>
/// Tracks active menu buttons and page registrations from the AppHost interaction service.
/// Shared between InteractionsProvider (which receives updates) and navigation components (which render them).
/// </summary>
public sealed class CustomInteractionState
{
    private readonly object _lock = new();
    private ImmutableArray<MenuButtonState> _menuButtons = [];
    private ImmutableArray<PageRegistrationState> _pages = [];

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

    public ImmutableArray<PageRegistrationState> Pages
    {
        get
        {
            lock (_lock)
            {
                return _pages;
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

    public void AddPage(int interactionId, string route, string title, IReadOnlyList<string> styleIncludes, IReadOnlyList<string> scriptIncludes)
    {
        lock (_lock)
        {
            // Idempotent — don't add if already registered.
            if (_pages.Any(p => p.InteractionId == interactionId))
            {
                return;
            }
            _pages = _pages.Add(new PageRegistrationState(interactionId, route, title, styleIncludes, scriptIncludes));
        }
    }

    public void RemovePage(int interactionId)
    {
        lock (_lock)
        {
            _pages = _pages.RemoveAll(p => p.InteractionId == interactionId);
        }
    }

    public PageRegistrationState? FindPageByRoute(string route)
    {
        lock (_lock)
        {
            return _pages.FirstOrDefault(p => string.Equals(p.Route, route, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void UpdatePageContent(int interactionId, string sessionId, string markdownContent)
    {
        OnPageContentUpdated?.Invoke(new PageContentUpdate(interactionId, sessionId, markdownContent));
    }
}

public sealed record MenuButtonState(int InteractionId, string IconName, string Text, string Tooltip, string Url);

public sealed record PageRegistrationState(int InteractionId, string Route, string Title, IReadOnlyList<string> StyleIncludes, IReadOnlyList<string> ScriptIncludes);

public sealed record PageContentUpdate(int InteractionId, string SessionId, string MarkdownContent);
