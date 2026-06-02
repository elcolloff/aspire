// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.Interaction;
using Aspire.Dashboard.Model.Markdown;
using Aspire.Dashboard.Resources;
using Aspire.DashboardService.Proto.V1;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Pages;

public partial class CustomPage : ComponentBase, IAsyncDisposable
{
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private MarkdownProcessor _markdownProcessor = default!;
    private string? _markdownContent;
    private string _pageTitle = "Page";
    private bool _pageNotFound;
    private bool _visitSent;
    private string? _currentRoute;
    private PageRegistrationState? _pageRegistration;

    [Parameter]
    public string? Route { get; set; }

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    [Inject]
    public required CustomInteractionState CustomInteractionState { get; init; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required IStringLocalizer<ControlsStrings> ControlsStringsLoc { get; init; }

    protected override void OnInitialized()
    {
        _markdownProcessor = InteractionMarkdownHelper.CreateProcessor(ControlsStringsLoc);
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
}
