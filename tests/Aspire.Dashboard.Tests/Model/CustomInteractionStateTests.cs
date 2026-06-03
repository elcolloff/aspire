// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.Interaction;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public class CustomInteractionStateTests
{
    [Fact]
    public void AddMenuButton_AddsToCollection()
    {
        var state = new CustomInteractionState();

        state.AddMenuButton(1, "Home", "Go Home", "Navigate home", "/pages/home");

        var button = Assert.Single(state.MenuButtons);
        Assert.Equal(1, button.InteractionId);
        Assert.Equal("Home", button.IconName);
        Assert.Equal("Go Home", button.Text);
        Assert.Equal("Navigate home", button.Tooltip);
        Assert.Equal("/pages/home", button.Url);
    }

    [Fact]
    public void AddMenuButton_Duplicate_IsIdempotent()
    {
        var state = new CustomInteractionState();

        state.AddMenuButton(1, "Home", "Go Home", "Navigate home", "/pages/home");
        state.AddMenuButton(1, "Home", "Go Home", "Navigate home", "/pages/home");

        Assert.Single(state.MenuButtons);
    }

    [Fact]
    public void AddMenuButton_RaisesOnMenuButtonsChanged()
    {
        var state = new CustomInteractionState();
        var eventRaised = false;
        state.OnMenuButtonsChanged += () => eventRaised = true;

        state.AddMenuButton(1, "Home", "Go Home", "Navigate home", "/pages/home");

        Assert.True(eventRaised);
    }

    [Fact]
    public void RemoveMenuButton_RemovesFromCollection()
    {
        var state = new CustomInteractionState();
        state.AddMenuButton(1, "Home", "Go Home", "Navigate home", "/pages/home");

        state.RemoveMenuButton(1);

        Assert.Empty(state.MenuButtons);
    }

    [Fact]
    public void RemoveMenuButton_RaisesOnMenuButtonsChanged()
    {
        var state = new CustomInteractionState();
        state.AddMenuButton(1, "Home", "Go Home", "Navigate home", "/pages/home");

        var eventRaised = false;
        state.OnMenuButtonsChanged += () => eventRaised = true;

        state.RemoveMenuButton(1);

        Assert.True(eventRaised);
    }

    [Fact]
    public void AddPage_AddsToCollection()
    {
        var state = new CustomInteractionState();

        state.AddPage(1, "my-page", "My Page", [], []);

        var page = Assert.Single(state.Pages);
        Assert.Equal(1, page.InteractionId);
        Assert.Equal("my-page", page.Route);
        Assert.Equal("My Page", page.Title);
    }

    [Fact]
    public void AddPage_Duplicate_IsIdempotent()
    {
        var state = new CustomInteractionState();

        state.AddPage(1, "my-page", "My Page", [], []);
        state.AddPage(1, "my-page", "My Page", [], []);

        Assert.Single(state.Pages);
    }

    [Fact]
    public void RemovePage_RemovesFromCollection()
    {
        var state = new CustomInteractionState();
        state.AddPage(1, "my-page", "My Page", [], []);

        state.RemovePage(1);

        Assert.Empty(state.Pages);
    }

    [Fact]
    public void FindPageByRoute_ExistingRoute_ReturnsPage()
    {
        var state = new CustomInteractionState();
        state.AddPage(1, "my-page", "My Page", [], []);

        var result = state.FindPageByRoute("my-page");

        Assert.NotNull(result);
        Assert.Equal(1, result.InteractionId);
        Assert.Equal("my-page", result.Route);
        Assert.Equal("My Page", result.Title);
    }

    [Fact]
    public void FindPageByRoute_CaseInsensitive()
    {
        var state = new CustomInteractionState();
        state.AddPage(1, "My-Page", "My Page", [], []);

        var result = state.FindPageByRoute("my-page");

        Assert.NotNull(result);
        Assert.Equal(1, result.InteractionId);
    }

    [Fact]
    public void FindPageByRoute_NonExistentRoute_ReturnsNull()
    {
        var state = new CustomInteractionState();
        state.AddPage(1, "my-page", "My Page", [], []);

        var result = state.FindPageByRoute("other-page");

        Assert.Null(result);
    }

    [Fact]
    public void UpdatePageContent_RaisesOnPageContentUpdated()
    {
        var state = new CustomInteractionState();
        PageContentUpdate? receivedUpdate = null;
        state.OnPageContentUpdated += update => receivedUpdate = update;

        state.UpdatePageContent(1, "session-1", "# Hello");

        Assert.NotNull(receivedUpdate);
        Assert.Equal(1, receivedUpdate.InteractionId);
        Assert.Equal("session-1", receivedUpdate.SessionId);
        Assert.Equal("# Hello", receivedUpdate.MarkdownContent);
    }

    [Fact]
    public void MultipleMenuButtons_TrackedIndependently()
    {
        var state = new CustomInteractionState();

        state.AddMenuButton(1, "Home", "Home", "Go home", "/home");
        state.AddMenuButton(2, "Settings", "Settings", "Open settings", "/settings");

        Assert.Equal(2, state.MenuButtons.Length);

        state.RemoveMenuButton(1);

        var remaining = Assert.Single(state.MenuButtons);
        Assert.Equal(2, remaining.InteractionId);
    }

    [Fact]
    public void MultiplePages_TrackedIndependently()
    {
        var state = new CustomInteractionState();

        state.AddPage(1, "page-1", "Page 1", [], []);
        state.AddPage(2, "page-2", "Page 2", [], []);

        Assert.Equal(2, state.Pages.Length);

        state.RemovePage(1);

        var remaining = Assert.Single(state.Pages);
        Assert.Equal(2, remaining.InteractionId);
    }
}
