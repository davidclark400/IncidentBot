using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace IncidentBot.Kafka;

public static partial class KafkaDashboardIdentity
{
    public static string Uid(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        var normalized = InvalidCharacter().Replace(profileId.ToLowerInvariant(), "-").Trim('-');
        normalized = RepeatedHyphen().Replace(normalized, "-");
        if (normalized.Length == 0) normalized = "profile";
        var candidate = $"incidentbot-kafka-{normalized}";
        if (candidate.Length <= 40 && string.Equals(normalized, profileId, StringComparison.Ordinal))
        {
            return candidate;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(profileId)))[..8];
        var maximumProfileLength = 40 - "incidentbot-kafka--".Length - hash.Length;
        normalized = normalized[..Math.Min(normalized.Length, maximumProfileLength)].TrimEnd('-');
        return $"incidentbot-kafka-{normalized}-{hash}";
    }

    [GeneratedRegex("[^a-z0-9-]+", RegexOptions.CultureInvariant)]
    private static partial Regex InvalidCharacter();

    [GeneratedRegex("-+", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedHyphen();
}
