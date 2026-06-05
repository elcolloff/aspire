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
    private ImmutableArray<PersistentIframeState> _persistentIframes = [];

    public event Action? OnMenuButtonsChanged;
    public event Action<PageContentUpdate>? OnPageContentUpdated;
    public event Action? OnPersistentIframesChanged;

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

    public ImmutableArray<PersistentIframeState> PersistentIframes
    {
        get
        {
            lock (_lock)
            {
                return _persistentIframes;
            }
        }
    }

    /// <summary>
    /// Registers or updates a persistent iframe for the given route. The iframe is kept alive in the
    /// DOM across page navigations so that its internal state (e.g. navigation within the iframe) is preserved.
    /// </summary>
    public void SetPersistentIframe(string route, string iframeUrl)
    {
        lock (_lock)
        {
            var existing = _persistentIframes.FirstOrDefault(f => string.Equals(f.Route, route, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                // URL already registered — no change needed.
                if (string.Equals(existing.IframeUrl, iframeUrl, StringComparison.Ordinal))
                {
                    return;
                }

                _persistentIframes = _persistentIframes.Replace(existing, existing with { IframeUrl = iframeUrl });
            }
            else
            {
                _persistentIframes = _persistentIframes.Add(new PersistentIframeState(route, iframeUrl));
            }
        }
        OnPersistentIframesChanged?.Invoke();
    }

    /// <summary>
    /// Removes a persistent iframe when the page is unregistered.
    /// </summary>
    public void RemovePersistentIframe(string route)
    {
        lock (_lock)
        {
            _persistentIframes = _persistentIframes.RemoveAll(f => string.Equals(f.Route, route, StringComparison.OrdinalIgnoreCase));
        }
        OnPersistentIframesChanged?.Invoke();
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

    public void UpdatePageContent(int interactionId, string route, string sessionId, string content, string title, IReadOnlyList<string> styleIncludes, IReadOnlyList<string> scriptIncludes, bool enableHtml, string? iframeUrl, bool iframePersistent)
    {
        OnPageContentUpdated?.Invoke(new PageContentUpdate(interactionId, route, sessionId, content, title, styleIncludes, scriptIncludes, enableHtml, iframeUrl, iframePersistent));
    }
}

public sealed record MenuButtonState(int InteractionId, string IconName, string Text, string Tooltip, string Url);

public sealed record PageContentUpdate(int InteractionId, string Route, string SessionId, string Content, string Title, IReadOnlyList<string> StyleIncludes, IReadOnlyList<string> ScriptIncludes, bool EnableHtml, string? IframeUrl, bool IframePersistent);

public sealed record PersistentIframeState(string Route, string IframeUrl);
