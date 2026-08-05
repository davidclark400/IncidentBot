using System.Text.RegularExpressions;
using PromQL.Parser;
using PromQL.Parser.Ast;
using PromQlParser = PromQL.Parser.Parser;

namespace Panko.Kafka;

internal static partial class KafkaPromQlRenderer
{
    private static readonly HashSet<string> AllowedPlaceholders = new(StringComparer.Ordinal)
    {
        "clusterRegex", "topicRegex", "consumerGroupRegex"
    };

    public static string Render(string template, KafkaRecipeScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["clusterRegex"] = RegexAlternation([scope.Cluster], "cluster"),
            ["topicRegex"] = RegexAlternation(scope.Topics, "topic"),
            ["consumerGroupRegex"] = scope.ConsumerGroups.Count == 0
                ? "a^"
                : RegexAlternation(scope.ConsumerGroups, "consumer group")
        };
        return ReplaceValidated(template, key => values[key]);
    }

    public static string RenderForGrafanaVariables(string template, KafkaRecipeScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return ReplaceValidated(
            template,
            key => key == "consumerGroupRegex" && scope.ConsumerGroups.Count == 0
                ? "a^"
                : "${" + key + ":regex}");
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

    public static IReadOnlyDictionary<string, IReadOnlySet<string>> ScopeLabelKeys(string template)
        => AnalyzeSelectorScopes(template, new HashSet<string>(StringComparer.Ordinal));

    internal static IReadOnlyDictionary<string, IReadOnlySet<string>> ValidateSelectorScopes(
        string template,
        IReadOnlySet<string> requiredPlaceholders)
        => AnalyzeSelectorScopes(template, requiredPlaceholders);

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
                    "Kafka PromQL must return a scalar or instant vector; " +
                    $"root expression type '{expressionType}' is not supported for range queries.");
            }
        }
        catch (Exception exception) when (
            exception is Superpower.ParseException or TypeChecker.InvalidTypeException)
        {
            throw new InvalidOperationException(
                $"Kafka PromQL is not a valid supported expression: {exception.Message}",
                exception);
        }

        var selectors = new DepthFirstExpressionVisitor()
            .GetExpressions(expression)
            .OfType<VectorSelector>()
            .ToArray();
        if (selectors.Length == 0)
        {
            throw new InvalidOperationException("Kafka PromQL must contain at least one vector selector.");
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
                    "Every Kafka PromQL vector selector must carry the metric resource scope; " +
                    $"selector '{selectorName}' is missing {string.Join(", ", missing)}.");
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
                    $"Kafka PromQL placeholder '{placeholder}' must belong to a parsed vector-selector regex matcher.");
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

    public static void ValidateScope(KafkaRecipeScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(scope.MetricPackId))
        {
            throw new InvalidOperationException("Kafka metricPackId is required.");
        }
        if (string.IsNullOrWhiteSpace(scope.Cluster))
        {
            throw new InvalidOperationException("Kafka cluster is required.");
        }
        if (scope.Topics.Count == 0)
        {
            throw new InvalidOperationException("Kafka requires at least one allowlisted topic.");
        }
        if (scope.Topics.Count > 200 || scope.ConsumerGroups.Count > 200)
        {
            throw new InvalidOperationException("Kafka topic and consumer-group allowlists are limited to 200 values each.");
        }

        ValidateResource(scope.Cluster, "cluster");
        ValidateList(scope.Topics, "topic");
        ValidateList(scope.ConsumerGroups, "consumer group");
    }

    private static string ReplaceValidated(string template, Func<string, string> replacement)
    {
        if (string.IsNullOrWhiteSpace(template) || template.Length > 4096)
        {
            throw new InvalidOperationException("Kafka PromQL must contain 1-4096 characters.");
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
                throw new InvalidOperationException($"Kafka PromQL placeholder '{key}' is not allowlisted.");
            }
            if (!safePlaceholderSpans.Contains((placeholder.Index, placeholder.Length)))
            {
                throw new InvalidOperationException(
                    $"Kafka PromQL placeholder '{key}' must be the complete value of a regex label matcher.");
            }
        }

        var rendered = Placeholder().Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            return replacement(key);
        });
        if (rendered.Contains("{{", StringComparison.Ordinal)
            || rendered.Contains("}}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Kafka PromQL contains a malformed or unsupported placeholder.");
        }
        return rendered;
    }

    private static string RegexAlternation(IEnumerable<string> values, string resource)
    {
        var ordered = values
            .Select(value => ValidateResource(value, resource))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            // PromQL double-quoted strings use Go escaping. A regex escape such as \.
            // therefore needs two backslashes in the PromQL source so the regex engine
            // ultimately receives one.
            .Select(value => Regex.Escape(value).Replace("\\", "\\\\", StringComparison.Ordinal))
            .ToArray();
        if (ordered.Length == 0)
        {
            throw new InvalidOperationException($"Kafka {resource} allowlist must not be empty.");
        }
        // Prometheus uses RE2, which supports capturing groups but not .NET-style
        // non-capturing groups.
        return "(" + string.Join('|', ordered) + ")";
    }

    private static void ValidateList(IReadOnlyList<string> values, string resource)
    {
        if (values.Any(string.IsNullOrWhiteSpace)
            || values.Distinct(StringComparer.Ordinal).Count() != values.Count)
        {
            throw new InvalidOperationException($"Kafka {resource} allowlist contains an empty or duplicate value.");
        }
        foreach (var value in values) ValidateResource(value, resource);
    }

    private static string ValidateResource(string value, string resource)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 256
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Any(char.IsControl)
            || value.IndexOfAny(['{', '}', '"', '`', '$', '[', ']']) >= 0
            || GrafanaKeyValueDelimiter().IsMatch(value))
        {
            throw new InvalidOperationException(
                $"Kafka {resource} value is empty, too long, or unsafe for PromQL/Grafana variable rendering.");
        }
        return value;
    }

    [GeneratedRegex(@"\{\{([A-Za-z][A-Za-z0-9]*)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    [GeneratedRegex(
        "(?<label>[A-Za-z_:][A-Za-z0-9_:]*)\\s*=~\\s*\"\\{\\{(?<placeholder>clusterRegex|topicRegex|consumerGroupRegex)\\}\\}\"",
        RegexOptions.CultureInvariant)]
    private static partial Regex ScopedLabelMatcher();

    [GeneratedRegex(@"\s+:\s+", RegexOptions.CultureInvariant)]
    private static partial Regex GrafanaKeyValueDelimiter();
}
