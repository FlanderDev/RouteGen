using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FlanderDev.RouteGen.Generators;

/// <summary>
/// Scans every <c>.razor</c> file passed to the compilation as an <c>AdditionalText</c> for
/// <c>@page "..."</c> directives and emits a strongly-typed static <c>Paths</c> class. A
/// component may declare more than one <c>@page</c> directive (an officially supported Blazor
/// pattern); every route beyond the first requires an explicit two-argument
/// <c>[GeneratedPathName("route", "Name")]</c> override, since there'd otherwise be no way to
/// tell which generated member name was meant for which route (see RG0012).
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class PageRouteGenerator : IIncrementalGenerator
{
    /// <summary>Matches every <c>@page "..."</c> directive and captures the route template.</summary>
    private static readonly Regex PageDirectiveRegex = new(
        "^\\s*@page\\s+\"([^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Matches every <c>@attribute [GeneratedPathName(...)]</c> directive, one or two string
    /// arguments: group 1 is always the first argument, group 2 is the second argument when
    /// present (the two-argument "route, name" form) or empty otherwise (the one-argument "name"
    /// form, which names the component's sole or first route).
    /// </summary>
    private static readonly Regex GeneratedPathNameRegex = new(
        "@attribute\\s+\\[\\s*GeneratedPathName\\s*\\(\\s*\"([^\"]+)\"\\s*(?:,\\s*\"([^\"]+)\")?\\s*\\)\\s*\\]",
        RegexOptions.Compiled);

    /// <inheritdoc cref="IIncrementalGenerator.Initialize"/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var razorFiles = context.AdditionalTextsProvider
            .Where(static text => text.Path.EndsWith(".razor", System.StringComparison.OrdinalIgnoreCase));

        var parsedPages = razorFiles
            .Select(static (text, ct) => ParsePages(text, ct))
            .SelectMany(static (pages, _) => pages);

        var rootNamespace = context.AnalyzerConfigOptionsProvider.Select(static (options, _) =>
            options.GlobalOptions.TryGetValue("build_property.RootNamespace", out var ns) &&
            !string.IsNullOrEmpty(ns)
                ? ns!
                : "Generated");

        var combined = parsedPages.Collect().Combine(rootNamespace);

        context.RegisterSourceOutput(combined, static (spc, pair) =>
        {
            var (pages, ns) = pair;

            if (pages.Length == 0) return;

            // Sorted before processing (not just before emission) so which entry "wins" a name
            // collision is deterministic across builds/machines, regardless of whatever order
            // Collect() happened to produce the underlying file list in.
            var ordered = pages
                .OrderBy(p => p.MemberName ?? string.Empty, System.StringComparer.Ordinal)
                .ThenBy(p => p.Route, System.StringComparer.Ordinal)
                .ToList();

            var used = new Dictionary<string, PageRouteInfo>();
            var members = new List<PageRouteInfo>();

            foreach (var page in ordered)
            {
                if (page.MemberName is null)
            {
                    // A route beyond the first with no matching two-argument [GeneratedPathName]
                    // override, there's no reasonable name to infer, so this is an error rather
                    // than a silently-dropped route (the bug this whole path exists to prevent).
                    spc.ReportDiagnostic(Diagnostic.Create(
                        RouteGenDiagnostics.UnnamedAdditionalPageRoute,
                        Location.None,
                        page.Route,
                        page.FilePath));
                    continue;
                }

                if (used.ContainsKey(page.MemberName))
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                            RouteGenDiagnostics.AmbiguousPageRouteMember,
                            Location.None,
                            page.MemberName));
                    continue;
                }

                used[page.MemberName] = page;
                members.Add(page);
            }

            if (members.Count == 0) return;

            spc.AddSource("Paths.g.cs", EmitPathsClass(ns, members));
        });
    }

    /// <summary>
    /// Extracts every <c>@page</c> route from a single <c>.razor</c> file. Returns one
    /// <see cref="PageRouteInfo"/> per route, in source order; the first route's
    /// <see cref="PageRouteInfo.MemberName"/> is always resolved (falling back to the filename),
    /// but every route after it has a null <see cref="PageRouteInfo.MemberName"/> unless a
    /// matching two-argument <c>[GeneratedPathName]</c> override was found for that exact route
    /// text, the caller turns an unresolved name into diagnostic RG0012.
    /// </summary>
    private static List<PageRouteInfo> ParsePages(
        AdditionalText text,
        System.Threading.CancellationToken ct)
    {
        var result = new List<PageRouteInfo>();

        var sourceText = text.GetText(ct);
        if (sourceText is null) return result;

        string content = sourceText.ToString();
        var pageMatches = PageDirectiveRegex.Matches(content);
        if (pageMatches.Count == 0) return result;

        string? legacyName = null;
        var routeNameOverrides = new Dictionary<string, string>(System.StringComparer.Ordinal);

        foreach (Match nameMatch in GeneratedPathNameRegex.Matches(content))
        {
            string first = nameMatch.Groups[1].Value;
            var secondGroup = nameMatch.Groups[2];

            if (secondGroup.Success)
            {
                // Two-argument form: (route, name), keyed by the exact route text.
                routeNameOverrides[first] = secondGroup.Value;
            }
            else
            {
                // One-argument form: (name), applies to the component's first route. If there
                // happens to be more than one of these on the same component, the first one found
                // wins; that's an unusual enough thing to write that it doesn't warrant its own
                // diagnostic.
                legacyName ??= first;
            }
        }

        string fileName = System.IO.Path.GetFileNameWithoutExtension(text.Path);

        for (int i = 0; i < pageMatches.Count; i++)
        {
            string route = pageMatches[i].Groups[1].Value;
        var template = RouteTemplateParser.Parse(route);

            string? memberName;
            if (routeNameOverrides.TryGetValue(route, out var explicitName))
            {
                memberName = explicitName;
            }
            else if (i == 0)
            {
                // The first route falls back to the filename-derived name (and may also use the
                // one-argument override), unchanged from before multi-route support existed, so
                // every existing single-route component behaves exactly as it did before.
                memberName = legacyName ?? SanitizeIdentifier(fileName);
            }
            else
            {
                memberName = null; // unresolved, RG0012 in the caller.
    }

            result.Add(new PageRouteInfo(memberName, route, template, text.Path));
        }

        return result;
    }

    /// <summary>Strips <paramref name="name"/> down to a valid C# identifier (letters, digits, underscores; never starting with a digit).</summary>
    private static string SanitizeIdentifier(string name)
    {
        var sb = new StringBuilder();

        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
                sb.Append(c);
        }

        if (sb.Length == 0) return "Page";
        if (char.IsDigit(sb[0])) sb.Insert(0, '_');

        return sb.ToString();
    }

    /// <summary>Renders the full source text of the generated <c>Paths</c> class for <paramref name="pages"/>.</summary>
    private static string EmitPathsClass(
        string rootNamespace,
        List<PageRouteInfo> pages)
    {
        var sb = new StringBuilder();

        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// GENERATED by RouteGen from @page directives — do not edit.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine();
        sb.Append("namespace ").Append(rootNamespace).AppendLine(";");
        sb.AppendLine();
        sb.AppendLine("    public static class Paths");
        sb.AppendLine("    {");

        foreach (var page in pages)
        {
            if (page.Template.Parameters.Count == 0)
            {
                sb.Append("        public const string ")
                  .Append(page.MemberName)
                  .Append(" = \"")
                  .Append(EscapeString(page.Route))
                  .AppendLine("\";");
            }
            else
            {
                var parameters = page.Template.Parameters
                    .Select(t => (
                        Name: SanitizeIdentifier(LowerFirst(t.Name)),
                        Type: MapConstraintToType(t.Constraint),
                        Token: t))
                    .ToList();

                string paramList = string.Join(
                    ", ",
                    parameters.Select(p => $"{p.Type} {p.Name}"));

                sb.Append("        public static string ")
                  .Append(page.MemberName)
                  .Append('(')
                  .Append(paramList)
                  .AppendLine(")");

                sb.Append("            => $\"")
                  .Append(BuildInterpolated(page.Template, parameters))
                  .AppendLine("\";");
            }
        }

        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>Builds the interpolated-string body for a parameterized page's generated method, substituting each route token with its matching parameter (URL-escaped for string-typed tokens).</summary>
    private static string BuildInterpolated(
        RouteTemplate template,
        List<(string Name, string Type, RouteParameterPart Token)> parameters)
    {
        var sb = new StringBuilder();

        foreach (var part in template.Parts)
        {
            if (part is RouteLiteralPart literal)
            {
                foreach (char c in literal.Text)
                {
                    if (c == '"' || c == '\\')
                        sb.Append('\\');

                    sb.Append(c);
                }

                continue;
            }

            var parameter = (RouteParameterPart)part;
            var match = parameters.FirstOrDefault(p =>
                string.Equals(
                    p.Token.Name,
                    parameter.Name,
                    System.StringComparison.OrdinalIgnoreCase));

            if (match.Name is not null)
            {
                if (match.Type == "string")
                    sb.Append("{Uri.EscapeDataString(").Append(match.Name).Append(")}");
                else
                    sb.Append('{').Append(match.Name).Append('}');
            }
        }

        return sb.ToString();
    }

    /// <summary>Maps an ASP.NET Core route constraint (e.g. "int") to the C# parameter type to generate; unrecognized/absent constraints default to <c>string</c>.</summary>
    private static string MapConstraintToType(string? constraint)
    {
        if (constraint is null) return "string";

        // Constraints can be chained, e.g. "int:min(1)" for {id:int:min(1)}, RouteTemplateParser
        // deliberately keeps the whole chain as one opaque string (by design; see its remarks),
        // so only the first segment (the one that actually determines the underlying CLR type;
        // everything chained after it, like min/max/range/regex, is a refinement that doesn't
        // change that type) should be used for this mapping. Matching the whole chain verbatim
        // would silently fall through to "string" for any constrained parameter, e.g. exactly the
        // {id:int:min(1)} example from ASP.NET Core's own routing documentation.
        int colon = RouteTemplateParser.FindTopLevel(constraint, ':');
        string primary = colon >= 0 ? constraint.Substring(0, colon) : constraint;

        return primary switch
    {
        "int" => "int",
        "long" => "long",
        "bool" => "bool",
        "double" => "double",
        "decimal" => "decimal",
        "guid" => "Guid",
        "datetime" => "DateTime",
        _ => "string"
    };
    }

    /// <summary>Lowercases the first character of <paramref name="s"/>, so route tokens become valid camelCase parameter names.</summary>
    private static string LowerFirst(string s) =>
        s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);

    /// <summary>Escapes backslashes and double quotes for embedding <paramref name="s"/> in a generated C# string literal.</summary>
    private static string EscapeString(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>A single parsed <c>@page</c> route, ready to emit as a <c>Paths</c> member.</summary>
    /// <param name="memberName">The generated member's name, or null if it couldn't be resolved (RG0012).</param>
    /// <param name="route">The raw route template from the <c>@page</c> directive.</param>
    /// <param name="template">The parsed route template.</param>
    /// <param name="filePath">The source <c>.razor</c> file's path, for diagnostic messages.</param>
    private sealed class PageRouteInfo(
        string? memberName,
        string route,
        RouteTemplate template,
        string filePath)
    {
        /// <summary>The generated member's name, or null if it couldn't be resolved (RG0012).</summary>
        public string? MemberName { get; } = memberName;

        /// <summary>The raw route template from the <c>@page</c> directive.</summary>
        public string Route { get; } = route;

        /// <summary>The parsed route template.</summary>
        public RouteTemplate Template { get; } = template;

        /// <summary>The source <c>.razor</c> file's path, for diagnostic messages.</summary>
        public string FilePath { get; } = filePath;
    }
}
