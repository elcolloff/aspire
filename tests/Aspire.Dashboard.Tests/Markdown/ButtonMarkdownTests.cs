// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.Markdown;
using Aspire.Dashboard.Resources;
using Xunit;

namespace Aspire.Dashboard.Tests.Markdown;

public class ButtonMarkdownTests
{
    [Fact]
    public void ButtonConfig_ParseInline_AllProperties()
    {
        var content = "type=button command=doSomething resource=my-resource arguments=id=123&name=test icon=Send";

        var config = ButtonConfig.ParseInline(content);

        Assert.Equal("doSomething", config.Values["command"]);
        Assert.Equal("my-resource", config.Values["resource"]);
        Assert.Equal("id=123&name=test", config.Values["arguments"]);
        Assert.Equal("Send", config.Icon);
        Assert.DoesNotContain(config.Values, kvp => kvp.Key == "type");
    }

    [Fact]
    public void ButtonConfig_ParseInline_ArgumentsWithMultipleEquals()
    {
        var content = "type=button command=echo arguments=message=Hello+World&count=42&flag=true";

        var config = ButtonConfig.ParseInline(content);

        Assert.Equal("echo", config.Values["command"]);
        Assert.Equal("message=Hello+World&count=42&flag=true", config.Values["arguments"]);
    }

    [Fact]
    public void ButtonConfig_ParseInline_CommandOnly()
    {
        var content = "type=button command=myCommand";

        var config = ButtonConfig.ParseInline(content);

        Assert.Equal("myCommand", config.Values["command"]);
        Assert.Null(config.Icon);
        Assert.DoesNotContain(config.Values, kvp => kvp.Key.Equals("resource", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ButtonConfig_ParseInline_CaseInsensitiveKeys()
    {
        var content = "TYPE=button COMMAND=action ICON=Download RESOURCE=res";

        var config = ButtonConfig.ParseInline(content);

        Assert.Equal("action", config.Values["command"]);
        Assert.Equal("Download", config.Icon);
        Assert.Equal("res", config.Values["resource"]);
    }

    [Fact]
    public void ButtonConfig_ParseInline_TypeKeySkipped()
    {
        var content = "type=button command=test";

        var config = ButtonConfig.ParseInline(content);

        Assert.DoesNotContain(config.Values, kvp => kvp.Key.Equals("type", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("test", config.Values["command"]);
    }

    [Fact]
    public void ButtonMarkdown_RendersButton()
    {
        var processor = CreateMarkdownProcessor();
        var markdown = "[Click Me](type=button command=doSomething resource=my-resource icon=Send)";

        var html = processor.ToHtml(markdown);

        Assert.Contains("<fluent-button", html);
        Assert.Contains("data-text=\"Click Me\"", html);
        Assert.Contains("data-command=\"doSomething\"", html);
        Assert.Contains("data-resource=\"my-resource\"", html);
        Assert.Contains("Click Me", html);
    }

    [Fact]
    public void ButtonMarkdown_WithArguments()
    {
        var processor = CreateMarkdownProcessor();
        var markdown = "[Delete](type=button command=delete-todo resource=todo-commands arguments=id=42)";

        var html = processor.ToHtml(markdown);

        Assert.Contains("<fluent-button", html);
        Assert.Contains("data-command=\"delete-todo\"", html);
        Assert.Contains("data-resource=\"todo-commands\"", html);
        Assert.Contains("data-arguments=\"id=42\"", html);
        Assert.Contains("Delete", html);
    }

    [Fact]
    public void ButtonMarkdown_InlineInParagraph()
    {
        var processor = CreateMarkdownProcessor();
        var markdown = "Click [here](type=button command=navigate resource=nav) to proceed.";

        var html = processor.ToHtml(markdown);

        Assert.Contains("<fluent-button", html);
        Assert.Contains("to proceed", html);
    }

    [Fact]
    public void ButtonMarkdown_RegularLinksNotAffected()
    {
        var processor = CreateMarkdownProcessor();
        var markdown = "[Google](https://google.com)";

        var html = processor.ToHtml(markdown);

        Assert.DoesNotContain("<fluent-button", html);
        Assert.Contains("<a", html);
        Assert.Contains("https://google.com", html);
    }

    [Fact]
    public void ButtonMarkdown_ComplexArguments()
    {
        var processor = CreateMarkdownProcessor();
        var markdown = "[Echo](type=button command=echo-args resource=cmds arguments=message=Hello+from+button&repeat=3&shout=true)";

        var html = processor.ToHtml(markdown);

        Assert.Contains("<fluent-button", html);
        Assert.Contains("data-command=\"echo-args\"", html);
        Assert.Contains("data-arguments=\"message=Hello+from+button&amp;repeat=3&amp;shout=true\"", html);
    }

    [Fact]
    public void ButtonMarkdown_MultipleButtons()
    {
        var processor = CreateMarkdownProcessor();
        var markdown = "[Save](type=button command=save) [Cancel](type=button command=cancel)";

        var html = processor.ToHtml(markdown);

        var buttonCount = System.Text.RegularExpressions.Regex.Matches(html, "<fluent-button").Count;
        Assert.Equal(2, buttonCount);
        Assert.Contains("data-command=\"save\"", html);
        Assert.Contains("data-command=\"cancel\"", html);
    }

    [Fact]
    public void ButtonMarkdown_WithoutIcon_NoSvg()
    {
        var processor = CreateMarkdownProcessor();
        var markdown = "[Submit](type=button command=submit_form)";

        var html = processor.ToHtml(markdown);

        Assert.Contains("<fluent-button", html);
        Assert.Contains("Submit", html);
        Assert.DoesNotContain("<svg slot=\"start\"", html);
    }

    [Fact]
    public void ButtonMarkdown_EmptyText_WithIcon_RendersIconOnly()
    {
        var processor = CreateMarkdownProcessor();
        var markdown = "[](type=button command=delete icon=Delete)";

        var html = processor.ToHtml(markdown);

        Assert.Contains("<fluent-button", html);
        Assert.Contains("data-command=\"delete\"", html);
        Assert.Contains("aria-label=\"Delete\"", html);
    }

    [Fact]
    public void ButtonMarkdown_EmptyText_NoIcon_NotRendered()
    {
        var processor = CreateMarkdownProcessor();
        var markdown = "[](type=button command=click)";

        var html = processor.ToHtml(markdown);

        Assert.DoesNotContain("<fluent-button", html);
    }

    [Fact]
    public void ButtonMarkdown_MissingClosingParen_NotParsed()
    {
        var processor = CreateMarkdownProcessor();
        var markdown = "[Click](type=button command=test";

        var html = processor.ToHtml(markdown);

        Assert.DoesNotContain("<fluent-button", html);
    }

    [Fact]
    public void ButtonMarkdown_SpecialCharactersInText()
    {
        var processor = CreateMarkdownProcessor();
        var markdown = "[Download & Share](type=button command=download)";

        var html = processor.ToHtml(markdown);

        Assert.Contains("Download &amp; Share", html);
    }

    [Fact]
    public void ButtonMarkdown_LinkWithoutTypeButton_NotAButton()
    {
        var processor = CreateMarkdownProcessor();
        var markdown = "[Click](command=test resource=res)";

        var html = processor.ToHtml(markdown);

        // Without type=button prefix, it should not be treated as a button
        Assert.DoesNotContain("<fluent-button", html);
    }

    internal static MarkdownProcessor CreateMarkdownProcessor()
    {
        return new MarkdownProcessor(
            new TestStringLocalizer<ControlsStrings>(),
            safeUrlSchemes: null,
            extensions: [new ButtonExtension()]);
    }
}
