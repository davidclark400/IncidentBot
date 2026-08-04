using System.Security.Cryptography;
using System.Text;
using Panko.Api.Domain;

namespace Panko.Api.Signatures;

public sealed class SignatureGenerator
{
    public const string AlgorithmVersion = "v1";

    public CaseSignature Generate(SignatureFeatures features, SignatureStage stage)
    {
        var family = Canonicalize(features, includeExact: false);
        var exact = Canonicalize(features, includeExact: true);
        var completeness = stage == SignatureStage.Provisional
            ? 0.35
            : Math.Clamp(0.45
                + (features.ErrorTemplates.Count > 0 ? 0.2 : 0)
                + (features.CodeLocations.Count > 0 ? 0.15 : 0)
                + (features.Components.Count > 1 ? 0.1 : 0)
                + (features.SymptomCategories.Count > 0 ? 0.1 : 0), 0, 1);
        return new CaseSignature(
            AlgorithmVersion,
            stage,
            Hash(family),
            Hash(exact),
            features,
            completeness);
    }

    internal static string Canonicalize(SignatureFeatures features, bool includeExact)
    {
        var parts = new List<string>
        {
            $"algorithm={AlgorithmVersion}",
            $"service={features.ServiceId}",
            $"recipe={features.RecipeId}",
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

    private static string Join(IEnumerable<string> values) => string.Join('|', values.Order(StringComparer.Ordinal));
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
