// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;

namespace Aspire.Cli.Scaffolding;

internal enum ScaffoldOptionsContext
{
    /// <summary>
    /// Options used when scaffolding into an existing application.
    /// </summary>
    Init,

    /// <summary>
    /// Options used when creating a new empty AppHost template.
    /// </summary>
    EmptyAppHostTemplate
}

/// <summary>
/// Provides language-specific default options for scaffold scenarios.
/// </summary>
internal static class ScaffoldOptions
{
    private const string TypeScriptIncludeESLint = "typescript.includeESLint";

    // Init targets existing projects, so avoid injecting linting policy unless the caller opts in.
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> s_initDefaults =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            [KnownLanguageId.TypeScript] = CreateTypeScriptOptions(includeESLint: false),
            [KnownLanguageId.TypeScriptAlias] = CreateTypeScriptOptions(includeESLint: false)
        };

    // Empty templates are greenfield projects, so include the TypeScript linting baseline by default.
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> s_emptyAppHostTemplateDefaults =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            [KnownLanguageId.TypeScript] = CreateTypeScriptOptions(includeESLint: true),
            [KnownLanguageId.TypeScriptAlias] = CreateTypeScriptOptions(includeESLint: true)
        };

    /// <summary>
    /// Gets the default scaffold options for the requested language and context.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? GetDefaults(LanguageInfo language, ScaffoldOptionsContext context)
    {
        var defaults = context switch
        {
            ScaffoldOptionsContext.Init => s_initDefaults,
            ScaffoldOptionsContext.EmptyAppHostTemplate => s_emptyAppHostTemplateDefaults,
            _ => throw new ArgumentOutOfRangeException(nameof(context), context, null)
        };

        return defaults.TryGetValue(language.LanguageId.Value, out var options) ? options : null;
    }

    private static IReadOnlyDictionary<string, string> CreateTypeScriptOptions(bool includeESLint) =>
        new Dictionary<string, string>
        {
            [TypeScriptIncludeESLint] = includeESLint.ToString()
        };
}
