using System.Text.RegularExpressions;
using PromQL.Parser;
using PromQL.Parser.Ast;
using PromQlParser = PromQL.Parser.Parser;

namespace Panko.Observability;

internal static partial class ServicePromQlRenderer
{
    private static readonly HashSet<string> AllowedPlaceholders = new(StringComparer.Ordinal)
    {
        "serviceRegex", "environmentRegex"
    };

    public static string Render(string template, ServiceMetricScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["serviceRegex"] = RegexValue(scope.Service, "service"),
            ["environmentRegex"] = RegexValue(scope.Environment, "environment")
        };
        return ReplaceValidated(template, key => values[key]);
    }

    public static IReadOnlySet<string> ValidateTemplate(string template)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        ReplaceValidated(template, key =>
        {
            found.Add(key);
            return "scope";
        });
        return found;
    }

    public static IReadOnlyDictionary<string, IReadOnlySet<string>> ValidateSelectorScopes(
        string template,
        IReadOnlySet<string> requiredPlaceholders) =>
        AnalyzeSelectorScopes(template, requiredPlaceholders);

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> AnalyzeSelectorScopes(
        string template,
        IReadOnlySet<string> requiredPlaceholders)
    {
        _ = ValidateTemplate(template);
        Expr expression;
        try
        {
            expression = PromQlParser.ParseExpression(template);
            var expressionType = expression.CheckType();
            if (expressionType is not (PromQL.Parser.ValueType.Scalar or PromQL.Parser.ValueType.Vector))
            {
                throw new InvalidOperationException(
                    "Service PromQL must return a scalar or instant vector; "
                    + $"root expression type '{expressionType}' is not supported for range queries.");
            }
        }
        catch (Exception exception) when (
            exception is Superpower.ParseException or TypeChecker.InvalidTypeException)
        {
            throw new InvalidOperationException(
                $"Service PromQL is not a valid supported expression: {exception.Message}",
                exception);
        }

        var selectors = new DepthFirstExpressionVisitor()
            .GetExpressions(expression)
            .OfType<VectorSelector>()
            .ToArray();
        if (selectors.Length == 0)
        {
            throw new InvalidOperationException("Service PromQL must contain at least one vector selector.");
        }

        var labels = AllowedPlaceholders.ToDictionary(
            placeholder => placeholder,
            _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);
        var parsedOccurrences = AllowedPlaceholders.ToDictionary(
            placeholder => placeholder,
            _ => 0,
            StringComparer.Ordinal);

        foreach (var selector in selectors)
        {
            var selectorPlaceholders = new HashSet<string>(StringComparer.Ordinal);
            foreach (var matcher in selector.LabelMatchers?.Matchers ?? [])
            {
                if (matcher.Operator != Operators.LabelMatch.Regexp
                    || !TryGetPlaceholder(matcher.Value.Value, out var placeholder))
                {
                    continue;
                }

                selectorPlaceholders.Add(placeholder);
                labels[placeholder].Add(matcher.LabelName);
                parsedOccurrences[placeholder]++;
            }

            var missing = requiredPlaceholders
                .Where(placeholder => !selectorPlaceholders.Contains(placeholder))
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (missing.Length > 0)
            {
                var selectorName = selector.MetricIdentifier?.Value ?? "label-only selector";
                throw new InvalidOperationException(
                    "Every service PromQL vector selector must carry service and environment scope; "
                    + $"selector '{selectorName}' is missing {string.Join(", ", missing)}.");
            }
        }

        var textualOccurrences = Placeholder().Matches(template)
            .Select(match => match.Groups[1].Value)
            .GroupBy(placeholder => placeholder, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        foreach (var (placeholder, count) in textualOccurrences)
        {
            if (parsedOccurrences[placeholder] != count)
            {
                throw new InvalidOperationException(
                    $"Service PromQL placeholder '{placeholder}' must belong to a parsed vector-selector regex matcher.");
            }
        }

        return labels.ToDictionary(
            item => item.Key,
            item => (IReadOnlySet<string>)item.Value,
            StringComparer.Ordinal);
    }

    private static bool TryGetPlaceholder(string value, out string placeholder)
    {
        if (value.StartsWith("{{", StringComparison.Ordinal)
            && value.EndsWith("}}", StringComparison.Ordinal)
            && value.Length > 4)
        {
            var candidate = value[2..^2];
            if (AllowedPlaceholders.Contains(candidate))
            {
                placeholder = candidate;
                return true;
            }
        }

        placeholder = "";
        return false;
    }

    public static void ValidateScope(ServiceMetricScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(scope.MetricPackId))
        {
            throw new InvalidOperationException("Service observability metricPackId is required.");
        }
        ValidateScopeValue(scope.Service, "service");
        ValidateScopeValue(scope.Environment, "environment");
        if (scope.ThresholdOverrides is null)
        {
            throw new InvalidOperationException("Service observability thresholdOverrides must be a mapping.");
        }
        if (scope.ThresholdOverrides.Count > 200)
        {
            throw new InvalidOperationException("Service observability threshold overrides are limited to 200 metrics.");
        }
    }

    private static string ReplaceValidated(string template, Func<string, string> replacement)
    {
        if (string.IsNullOrWhiteSpace(template) || template.Length > 4096)
        {
            throw new InvalidOperationException("Service PromQL must contain 1-4096 characters.");
        }

        var placeholders = Placeholder().Matches(template);
        var safePlaceholderSpans = ScopedLabelMatcher().Matches(template)
            .Select(match => match.Groups["placeholder"])
            .Select(capture => (Index: capture.Index - 2, Length: capture.Length + 4))
            .ToHashSet();
        foreach (Match placeholder in placeholders)
        {
            var key = placeholder.Groups[1].Value;
            if (!AllowedPlaceholders.Contains(key))
            {
                throw new InvalidOperationException($"Service PromQL placeholder '{key}' is not allowlisted.");
            }
            if (!safePlaceholderSpans.Contains((placeholder.Index, placeholder.Length)))
            {
                throw new InvalidOperationException(
                    $"Service PromQL placeholder '{key}' must be the complete value of a regex label matcher.");
            }
        }

        var rendered = Placeholder().Replace(template, match => replacement(match.Groups[1].Value));
        if (rendered.Contains("{{", StringComparison.Ordinal)
            || rendered.Contains("}}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Service PromQL contains a malformed or unsupported placeholder.");
        }
        return rendered;
    }

    private static string RegexValue(string value, string scope)
    {
        ValidateScopeValue(value, scope);
        var escaped = Regex.Escape(value).Replace("\\", "\\\\", StringComparison.Ordinal);
        return "(" + escaped + ")";
    }

    private static void ValidateScopeValue(string value, string scope)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 256
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Any(char.IsControl)
            || value.IndexOfAny(['{', '}', '"', '`', '$', '[', ']']) >= 0
            || GrafanaKeyValueDelimiter().IsMatch(value))
        {
            throw new InvalidOperationException(
                $"Service observability {scope} value is empty, too long, or unsafe for PromQL rendering.");
        }
    }

    [GeneratedRegex(@"\{\{([A-Za-z][A-Za-z0-9]*)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    [GeneratedRegex(
        "(?<label>[A-Za-z_:][A-Za-z0-9_:]*)\\s*=~\\s*\"\\{\\{(?<placeholder>serviceRegex|environmentRegex)\\}\\}\"",
        RegexOptions.CultureInvariant)]
    private static partial Regex ScopedLabelMatcher();

    [GeneratedRegex(@"\s+:\s+", RegexOptions.CultureInvariant)]
    private static partial Regex GrafanaKeyValueDelimiter();
}
