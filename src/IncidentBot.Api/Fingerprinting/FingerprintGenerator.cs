using System.Security.Cryptography;
using System.Text;
using IncidentBot.Api.Domain;

namespace IncidentBot.Api.Fingerprinting;

public sealed class FingerprintGenerator
{
    public const string AlgorithmVersion = "v1";

    public IncidentFingerprint Generate(FingerprintFeatures features, FingerprintStage stage)
    {
        var family = Canonicalize(features, includeExact: false);
        var exact = Canonicalize(features, includeExact: true);
        var completeness = stage == FingerprintStage.Provisional
            ? 0.35
            : Math.Clamp(0.45
                + (features.ErrorTemplates.Count > 0 ? 0.2 : 0)
                + (features.CodeLocations.Count > 0 ? 0.15 : 0)
                + (features.Components.Count > 1 ? 0.1 : 0)
                + (features.SymptomCategories.Count > 0 ? 0.1 : 0), 0, 1);
        return new IncidentFingerprint(
            AlgorithmVersion,
            stage,
            Hash(family),
            Hash(exact),
            features,
            completeness);
    }

    internal static string Canonicalize(FingerprintFeatures features, bool includeExact)
    {
        var parts = new List<string>
        {
            $"algorithm={AlgorithmVersion}",
            $"service={features.ServiceId}",
            $"profile={features.ProfileId}",
            $"scopes={Join(features.Scopes)}",
            $"title={features.NormalizedTitle}",
            $"titleTokens={Join(features.TitleTokens)}",
            $"symptoms={Join(features.SymptomCategories)}",
            $"components={Join(features.Components)}"
        };
        if (includeExact)
        {
            parts.Add($"errors={Join(features.ErrorTemplates)}");
            parts.Add($"code={Join(features.CodeLocations)}");
        }
        return string.Join('\n', parts);
    }

    public static string ProblemKey(FingerprintFeatures features, string familyHash)
    {
        var service = KeyPart(features.ServiceId);
        var component = features.Components
            .FirstOrDefault(value => !string.Equals(value, features.ServiceId, StringComparison.Ordinal))
            ?? features.TitleTokens.FirstOrDefault()
            ?? "PROBLEM";
        return $"{service}-{KeyPart(component)}-{familyHash[..4].ToUpperInvariant()}";
    }

    private static string Join(IEnumerable<string> values) => string.Join('|', values.Order(StringComparer.Ordinal));
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string KeyPart(string value)
    {
        var cleaned = new string(value.Where(char.IsLetterOrDigit).Take(12).ToArray()).ToUpperInvariant();
        return string.IsNullOrEmpty(cleaned) ? "PROBLEM" : cleaned;
    }
}
