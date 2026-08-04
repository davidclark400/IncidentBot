using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Panko.Observability;

public static partial class ServiceDashboardIdentity
{
    public static string Uid(string recipeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipeId);
        var normalized = InvalidCharacter().Replace(recipeId.ToLowerInvariant(), "-").Trim('-');
        normalized = RepeatedHyphen().Replace(normalized, "-");
        if (normalized.Length == 0) normalized = "recipe";

        const string prefix = "panko-service-";
        var candidate = prefix + normalized;
        if (candidate.Length <= 40
            && string.Equals(normalized, recipeId, StringComparison.Ordinal))
        {
            return candidate;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(recipeId)))[..8];
        var maximumRecipeLength = 40 - prefix.Length - 1 - hash.Length;
        normalized = normalized[..Math.Min(normalized.Length, maximumRecipeLength)].TrimEnd('-');
        if (normalized.Length == 0) normalized = "recipe";
        return $"{prefix}{normalized}-{hash}";
    }

    [GeneratedRegex("[^a-z0-9-]+", RegexOptions.CultureInvariant)]
    private static partial Regex InvalidCharacter();

    [GeneratedRegex("-+", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedHyphen();
}
