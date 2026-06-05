// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Dashboard.Model.Interaction;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components.Layout;

public partial class PersistentIframeContainer : ComponentBase, IAsyncDisposable
{
    private ImmutableArray<PersistentIframeState> _iframes = [];
    private string? _activeRoute;
    private IJSObjectReference? _jsModule;
    private ElementReference _containerRef;
    private readonly HashSet<string> _monitoredRoutes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _healthyRoutes = new(StringComparer.OrdinalIgnoreCase);
    private DotNetObjectReference<PersistentIframeContainer>? _dotNetRef;

    [Inject]
    public required CustomInteractionState CustomInteractionState { get; init; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    protected override void OnInitialized()
    {
        _iframes = CustomInteractionState.PersistentIframes;
        CustomInteractionState.OnPersistentIframesChanged += OnIframesChanged;
        NavigationManager.LocationChanged += OnLocationChanged;
        UpdateActiveRoute();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Only interact with JS when the active route is a persistent iframe.
        if (_activeRoute is null || !_iframes.Any(f => string.Equals(f.Route, _activeRoute, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _jsModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Layout/PersistentIframeContainer.razor.js");
        _dotNetRef ??= DotNetObjectReference.Create(this);

        // Monitor the iframe if it hasn't been monitored yet.
        if (_monitoredRoutes.Add(_activeRoute))
        {
            await _jsModule.InvokeVoidAsync("monitorIframe", _containerRef, _activeRoute, _dotNetRef);
        }
        else if (!_healthyRoutes.Contains(_activeRoute))
        {
            // Only reload if the iframe is not healthy.
            await _jsModule.InvokeVoidAsync("reloadIframe", _containerRef, _activeRoute);
        }
    }

    [JSInvokable]
    public void OnIframeHealthy(string route)
    {
        _healthyRoutes.Add(route);
    }

    private void OnIframesChanged()
    {
        _iframes = CustomInteractionState.PersistentIframes;
        InvokeAsync(StateHasChanged);
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        UpdateActiveRoute();
        InvokeAsync(StateHasChanged);
    }

    private void UpdateActiveRoute()
    {
        // Extract route from the current URL: /pages/{route}
        var uri = new Uri(NavigationManager.Uri);
        var path = uri.AbsolutePath;
        if (path.StartsWith("/pages/", StringComparison.OrdinalIgnoreCase))
        {
            _activeRoute = path["/pages/".Length..];
        }
        else
        {
            _activeRoute = null;
        }
    }

    private bool IsActive(string route)
    {
        return string.Equals(_activeRoute, route, StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        CustomInteractionState.OnPersistentIframesChanged -= OnIframesChanged;
        NavigationManager.LocationChanged -= OnLocationChanged;

        _dotNetRef?.Dispose();

        if (_jsModule is not null)
        {
            await _jsModule.DisposeAsync();
        }
    }
}
