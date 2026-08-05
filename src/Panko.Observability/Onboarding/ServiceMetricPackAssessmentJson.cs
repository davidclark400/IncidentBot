using System.Text.Json;
using System.Text.Json.Serialization;

namespace Panko.Observability.Onboarding;

public static class ServiceMetricPackAssessmentJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static string Serialize(ServiceMetricPackAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        return JsonSerializer.Serialize(assessment, Options).ReplaceLineEndings("\n") + "\n";
    }
}
