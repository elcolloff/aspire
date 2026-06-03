// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.Interaction;
using Aspire.Dashboard.Model.Markdown;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Utils;
using Aspire.DashboardService.Proto.V1;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components.Pages;

public partial class CustomPage : ComponentBase, IAsyncDisposable
{
    private const string CustomPageContainerId = "custom-page-container";

    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private MarkdownProcessor _markdownProcessor = default!;
    private string? _markdownContent;
    private string _pageTitle = "Page";
    private bool _pageNotFound;
    private bool _visitSent;
    private bool _pageAssetsChanged;
    private string? _currentRoute;
    private PageRegistrationState? _pageRegistration;
    private IJSObjectReference? _jsModule;
    private List<string>? _activeCssHrefs;
    private List<IJSObjectReference>? _pageScriptModules;
    private DotNetObjectReference<CustomPageInterop>? _interopReference;

    [Parameter]
    public string? Route { get; set; }

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    [Inject]
    public required CustomInteractionState CustomInteractionState { get; init; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required IconResolver IconResolver { get; init; }

    [Inject]
    public required IStringLocalizer<ControlsStrings> ControlsStringsLoc { get; init; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    [Inject]
    public required ILogger<CustomPage> Logger { get; init; }

    [Inject]
    public required DashboardCommandExecutor DashboardCommandExecutor { get; init; }

    protected override void OnInitialized()
    {
        _markdownProcessor = InteractionMarkdownHelper.CreateProcessor(ControlsStringsLoc, [new ButtonExtension(IconResolver)]);
        _interopReference = DotNetObjectReference.Create(new CustomPageInterop(this));
        CustomInteractionState.OnPageContentUpdated += OnPageContentUpdated;
    }

    protected override async Task OnParametersSetAsync()
    {
        if (Route is null)
        {
            _pageNotFound = true;
            return;
        }

        // When the route changes (navigating between custom pages), send a leave for the old page
        // and reset state so the new page gets a fresh visit notification.
        if (_visitSent && !string.Equals(_currentRoute, Route, StringComparison.OrdinalIgnoreCase))
        {
            await SendPageLeaveAsync().ConfigureAwait(false);
            _visitSent = false;
            _markdownContent = null;
            _pageAssetsChanged = true;
        }

        _currentRoute = Route;

        _pageRegistration = CustomInteractionState.FindPageByRoute(Route);
        if (_pageRegistration is null)
        {
            _pageNotFound = true;
            return;
        }

        _pageNotFound = false;
        _pageTitle = _pageRegistration.Title;

        if (!_visitSent)
        {
            _visitSent = true;

            // Notify the host that a visitor has arrived at this page.
            var request = new WatchInteractionsRequestUpdate
            {
                InteractionId = _pageRegistration.InteractionId,
                PageVisit = new InteractionPageVisit
                {
                    SessionId = _sessionId
                }
            };

            // Extract query string parameters from the current URL.
            var uri = new Uri(NavigationManager.Uri);
            var queryParams = QueryHelpers.ParseQuery(uri.Query);
            foreach (var (key, values) in queryParams)
            {
                request.PageVisit.QueryParameters[key] = values.ToString();
            }

            await DashboardClient.SendInteractionRequestAsync(request, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private void OnPageContentUpdated(PageContentUpdate update)
    {
        if (_pageRegistration is null)
        {
            return;
        }

        // Only process updates for our interaction and session.
        if (update.InteractionId == _pageRegistration.InteractionId &&
            string.Equals(update.SessionId, _sessionId, StringComparison.Ordinal))
        {
            _markdownContent = update.MarkdownContent;
            InvokeAsync(StateHasChanged);
        }
    }

    public async ValueTask DisposeAsync()
    {
        CustomInteractionState.OnPageContentUpdated -= OnPageContentUpdated;
        _interopReference?.Dispose();

        await RemovePageAssetsAsync();
        await JSInteropHelpers.SafeDisposeAsync(_jsModule);

        if (_pageRegistration is not null && _visitSent)
        {
            await SendPageLeaveAsync().ConfigureAwait(false);
        }
    }

    private async Task SendPageLeaveAsync()
    {
        if (_pageRegistration is null)
        {
            return;
        }

        var request = new WatchInteractionsRequestUpdate
        {
            InteractionId = _pageRegistration.InteractionId,
            PageLeave = new InteractionPageLeave
            {
                SessionId = _sessionId
            }
        };

        try
        {
            await DashboardClient.SendInteractionRequestAsync(request, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort — the host may already be gone.
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _jsModule = await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/CustomPage.razor.js");
            await _jsModule.InvokeVoidAsync("attachButtonClickEvent", CustomPageContainerId, _interopReference);
            await AddPageAssetsAsync();
        }
        else if (_pageAssetsChanged)
        {
            _pageAssetsChanged = false;
            await RemovePageAssetsAsync();
            await AddPageAssetsAsync();
        }
    }

    /// <summary>
    /// Adds page-specific CSS links and loads script modules for the current page registration.
    /// </summary>
    private async Task AddPageAssetsAsync()
    {
        if (_jsModule is null || _pageRegistration is null)
        {
            return;
        }

        if (_pageRegistration.CssRoutes is { Count: > 0 } cssRoutes)
        {
            _activeCssHrefs = new List<string>(cssRoutes.Count);
            foreach (var cssRoute in cssRoutes)
            {
                var href = $"/pages/assets/{cssRoute}";
                await _jsModule.InvokeVoidAsync("addStylesheetLink", href);
                _activeCssHrefs.Add(href);
            }
        }

        if (_pageRegistration.ScriptRoutes is { Count: > 0 } scriptRoutes)
        {
            _pageScriptModules = new List<IJSObjectReference>(scriptRoutes.Count);
            foreach (var scriptRoute in scriptRoutes)
            {
                var module = await JS.InvokeAsync<IJSObjectReference>("import", $"/pages/assets/{scriptRoute}");
                _pageScriptModules.Add(module);
            }
        }
    }

    /// <summary>
    /// Removes previously added CSS links and disposes script modules.
    /// </summary>
    private async Task RemovePageAssetsAsync()
    {
        if (_pageScriptModules is not null)
        {
            foreach (var module in _pageScriptModules)
            {
                await JSInteropHelpers.SafeDisposeAsync(module);
            }
            _pageScriptModules = null;
        }

        if (_jsModule is not null && _activeCssHrefs is not null)
        {
            foreach (var href in _activeCssHrefs)
            {
                try
                {
                    await _jsModule.InvokeVoidAsync("removeStylesheetLink", href);
                }
                catch (Exception)
                {
                    // Best effort — JS runtime may be disconnected.
                }
            }
            _activeCssHrefs = null;
        }
    }

    /// <summary>
    /// Handles button clicks from markdown-rendered buttons in the custom page.
    /// </summary>
    private sealed class CustomPageInterop
    {
        private readonly CustomPage _page;

        public CustomPageInterop(CustomPage page)
        {
            _page = page;
        }

        [JSInvokable]
        public async Task OnButtonClick(IDictionary<string, string> values)
        {
            values.TryGetValue("command", out var commandName);
            values.TryGetValue("resource", out var resourceName);
            values.TryGetValue("arguments", out var argumentsValue);

            if (string.IsNullOrEmpty(commandName) || string.IsNullOrEmpty(resourceName))
            {
                _page.Logger.LogDebug("Button click missing required values. Command: {Command}, Resource: {Resource}", commandName, resourceName);
                return;
            }

            Dictionary<string, Google.Protobuf.WellKnownTypes.Value>? arguments = null;
            if (!string.IsNullOrEmpty(argumentsValue))
            {
                try
                {
                    var parsed = QueryHelpers.ParseQuery(argumentsValue);
                    arguments = parsed.ToDictionary(
                        kvp => kvp.Key,
                        kvp => Google.Protobuf.WellKnownTypes.Value.ForString(kvp.Value.ToString()));
                }
                catch (Exception ex)
                {
                    _page.Logger.LogDebug(ex, "Failed to parse button arguments as query string: {Arguments}", argumentsValue);
                    return;
                }
            }

            var resource = _page.DashboardClient.GetResource(resourceName);
            if (resource is null)
            {
                _page.Logger.LogDebug("Resource not found: {Resource}", resourceName);
                return;
            }

            var command = resource.Commands.FirstOrDefault(c => string.Equals(c.Name, commandName, StringComparison.Ordinal));
            if (command is null)
            {
                _page.Logger.LogDebug("Command {Command} not found on resource {Resource}", commandName, resourceName);
                return;
            }

            await _page.DashboardCommandExecutor.ExecuteAsync(resource, command, r => r.Name, arguments);
        }
    }
}
