using System;
using System.Collections.Generic;
using System.Text;

namespace FlanderDev.RouteGen.Generators;

/// <summary>
/// A parsed route template. The parser deliberately keeps ASP.NET Core route constraints as
/// opaque strings; the generator consumes only the structural information it needs.
/// </summary>
internal sealed class RouteTemplate(
    string original,
    IReadOnlyList<RouteTemplatePart> parts,
    IReadOnlyList<RouteParameterPart> parameters)
{
    public string Original { get; } = original;
    public IReadOnlyList<RouteTemplatePart> Parts { get; } = parts;
    public IReadOnlyList<RouteParameterPart> Parameters { get; } = parameters;
}

/// <summary>A single literal or route-parameter part of a route template.</summary>
internal abstract class RouteTemplatePart;

/// <summary>Literal text in a route template.</summary>
internal sealed class RouteLiteralPart(string text) : RouteTemplatePart
{
    public string Text { get; } = text;
}

/// <summary>
/// A route parameter such as {id}, {id:int}, {id?}, or {id:int=1}.
/// Constraint/default text is kept opaque so the parser does not need to know every ASP.NET
/// Core constraint that may be added in the future.
/// </summary>
internal sealed class RouteParameterPart(
    string name,
    string? constraint,
    bool optional,
    string? defaultValue) : RouteTemplatePart
{
    public string Name { get; } = name;
    public string? Constraint { get; } = constraint;
    public bool Optional { get; } = optional;
    public string? DefaultValue { get; } = defaultValue;
}

/// <summary>
/// Parser for the structural subset of ASP.NET Core route templates needed by RouteGen.
/// Escaped literal braces ({{ and }}) are treated as literal characters.
/// </summary>
internal static class RouteTemplateParser
{
    public static RouteTemplate Parse(string template)
    {
        var parts = new List<RouteTemplatePart>();
        var parameters = new List<RouteParameterPart>();
        var literal = new StringBuilder();

        int i = 0;
        while (i < template.Length)
        {
            char c = template[i];

            if (c == '{')
            {
                // ASP.NET Core uses {{ to represent a literal '{'.
                if (i + 1 < template.Length && template[i + 1] == '{')
                {
                    literal.Append('{');
                    i += 2;
                    continue;
                }

                FlushLiteral(parts, literal);

                int close = FindParameterEnd(template, i + 1);
                if (close < 0)
                {
                    // Keep malformed input as literal text. Diagnostics can be added by callers
                    // without making the parser throw during source generation.
                    literal.Append(template[i]);
                    i++;
                    continue;
                }

                string inner = template.Substring(i + 1, close - i - 1);
                i = close + 1;

                if (inner.Length == 0)
                {
                    literal.Append("{}");
                    continue;
                }

                if (!TryParseParameter(inner, out var parameter))
                {
                    literal.Append('{').Append(inner).Append('}');
                    continue;
                }

                parts.Add(parameter);
                parameters.Add(parameter);
                continue;
            }

            if (c == '}')
            {
                // ASP.NET Core uses }} to represent a literal '}'.
                if (i + 1 < template.Length && template[i + 1] == '}')
                {
                    literal.Append('}');
                    i += 2;
                    continue;
                }

                literal.Append(c);
                i++;
                continue;
            }

            literal.Append(c);
            i++;
        }

        FlushLiteral(parts, literal);
        return new RouteTemplate(template, parts, parameters);
    }

    /// <summary>
    /// Compatibility helper for existing generator code. New code should consume Parse().Parts
    /// and Parse().Parameters directly.
    /// </summary>
    public static IReadOnlyList<RouteParameterPart> ExtractTokens(string template) =>
        Parse(template).Parameters;

    /// <summary>Combines a base route and a suffix into a single template, normalizing slashes.</summary>
    public static string Combine(string baseRoute, string? suffix)
    {
        if (string.IsNullOrEmpty(suffix)) return baseRoute;

        var sb = new StringBuilder(baseRoute.TrimEnd('/'));
        sb.Append('/');
        sb.Append(suffix!.TrimStart('/'));
        return sb.ToString();
    }

    private static void FlushLiteral(List<RouteTemplatePart> parts, StringBuilder literal)
    {
        if (literal.Length == 0) return;
        parts.Add(new RouteLiteralPart(literal.ToString()));
        literal.Clear();
    }

    private static int FindParameterEnd(string template, int start)
    {
        int parenthesisDepth = 0;

        for (int i = start; i < template.Length; i++)
        {
            char c = template[i];

            if (c == '(')
            {
                parenthesisDepth++;
                continue;
            }

            if (c == ')' && parenthesisDepth > 0)
            {
                parenthesisDepth--;
                continue;
            }

            if (c == '}' && parenthesisDepth == 0)
                return i;

            // A doubled brace inside a parameter is literal text for the route template.
            if (c == '{' && i + 1 < template.Length && template[i + 1] == '{')
                i++;
        }

        return -1;
    }

    private static bool TryParseParameter(string inner, out RouteParameterPart parameter)
    {
        bool optional = inner.EndsWith("?", StringComparison.Ordinal);
        if (optional)
            inner = inner.Substring(0, inner.Length - 1);

        string? defaultValue = null;
        int equals = FindTopLevel(inner, '=');
        if (equals >= 0)
        {
            defaultValue = inner.Substring(equals + 1);
            inner = inner.Substring(0, equals);
        }

        string name = inner;
        string? constraint = null;

        int colon = FindTopLevel(inner, ':');
        if (colon >= 0)
        {
            name = inner.Substring(0, colon);
            constraint = inner.Substring(colon + 1);
        }

        name = name.Trim();
        if (name.Length == 0)
        {
            parameter = null!;
            return false;
        }

        parameter = new RouteParameterPart(name, constraint, optional, defaultValue);
        return true;
    }

    private static int FindTopLevel(string value, char target)
    {
        int parenthesisDepth = 0;

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '(')
            {
                parenthesisDepth++;
            }
            else if (c == ')' && parenthesisDepth > 0)
            {
                parenthesisDepth--;
            }
            else if (c == target && parenthesisDepth == 0)
            {
                return i;
            }
        }

        return -1;
    }
}
