using System.Text.RegularExpressions;

namespace Panko.Api.Security;

public sealed partial class SafeTemplateRenderer
{
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
    {
        "service", "environment", "cluster", "region", "component"
    };

    public string Render(string template, IReadOnlyDictionary<string, string> values)
    {
        return Placeholder().Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            if (!AllowedKeys.Contains(key) || !values.TryGetValue(key, out var value))
            {
                throw new InvalidOperationException($"Template placeholder '{key}' is not available or allowlisted.");
            }

            if (!SafeValue().IsMatch(value))
            {
                throw new InvalidOperationException($"Template value for '{key}' contains unsafe characters.");
            }

            return value;
        });
    }

    [GeneratedRegex(@"\{\{([a-z_]+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^[a-zA-Z0-9_.:/-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeValue();
}
