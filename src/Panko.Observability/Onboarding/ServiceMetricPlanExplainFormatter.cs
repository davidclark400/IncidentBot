using System.Globalization;
using System.Text;

namespace Panko.Observability.Onboarding;

public static class ServiceMetricPlanExplainFormatter
{
    public static string Format(string recipeId, ServiceMetricPlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipeId);
        ArgumentNullException.ThrowIfNull(plan);

        var output = new StringBuilder();
        output.AppendLine($"Recipe: {recipeId}");
        output.AppendLine($"Service: {plan.Service}");
        output.AppendLine($"Environment: {plan.Environment}");
        output.AppendLine($"Metric pack: {plan.MetricPackId} ({plan.MetricPackTitle})");
        output.AppendLine($"Contract: {plan.Contract}");
        output.AppendLine($"Metrics: {plan.Metrics.Length.ToString(CultureInfo.InvariantCulture)}");

        foreach (var metric in plan.Metrics)
        {
            output.AppendLine();
            output.AppendLine($"[{metric.Id}] {metric.Title}");
            output.AppendLine($"  role: {metric.Role}");
            output.AppendLine($"  requirement: {metric.Requirement}");
            output.AppendLine($"  crumbMode: {metric.CrumbMode}");
            output.AppendLine($"  dashboardRow: {metric.DashboardRow}");
            output.AppendLine($"  datasourceUid: {metric.DatasourceUid}");
            output.AppendLine($"  unit: {metric.Unit}");
            output.AppendLine($"  timeReducer: {metric.TimeReducer}");
            if (metric.Thresholds is null)
            {
                output.AppendLine("  thresholds: none");
            }
            else
            {
                output.AppendLine(
                    "  thresholds: warning="
                    + metric.Thresholds.Warning.ToString("R", CultureInfo.InvariantCulture)
                    + ", critical="
                    + metric.Thresholds.Critical.ToString("R", CultureInfo.InvariantCulture)
                    + $", direction={metric.Thresholds.Direction}");
            }
            output.AppendLine("  promQl:");
            foreach (var line in metric.PromQl.ReplaceLineEndings("\n").Split('\n'))
            {
                output.AppendLine("    " + line);
            }
        }

        return output.ToString().ReplaceLineEndings("\n");
    }
}
