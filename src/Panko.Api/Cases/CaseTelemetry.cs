using System.Diagnostics.Metrics;

namespace Panko.Api.Cases;

public sealed class CaseTelemetry
{
    public const string MeterName = "Panko.Cases";

    private readonly Meter meter = new(MeterName);
    private readonly Counter<long> casesCreated;
    private readonly Counter<long> crumbsAccepted;
    private readonly Counter<long> crumbsDeduplicated;
    private readonly Counter<long> crumbsRejected;
    private readonly Histogram<long> projectionLag;
    private readonly Histogram<double> projectionDuration;
    private readonly Counter<long> projectionRetries;
    private readonly Histogram<double> sourceRefreshDuration;
    private readonly Histogram<double> analysisDuration;
    private readonly Counter<long> llmCallsAvoided;
    private readonly Counter<long> mcpCommands;
    private readonly Counter<long> mcpFailures;

    public CaseTelemetry()
    {
        casesCreated = meter.CreateCounter<long>("panko.cases.created");
        crumbsAccepted = meter.CreateCounter<long>("panko.case_crumbs.accepted");
        crumbsDeduplicated = meter.CreateCounter<long>("panko.case_crumbs.deduplicated");
        crumbsRejected = meter.CreateCounter<long>("panko.case_crumbs.rejected");
        projectionLag = meter.CreateHistogram<long>("panko.case_file_projection.input_lag");
        projectionDuration = meter.CreateHistogram<double>("panko.case_file_projection.duration", "s");
        projectionRetries = meter.CreateCounter<long>("panko.case_file_projection.retries");
        sourceRefreshDuration = meter.CreateHistogram<double>("panko.case_source_refresh.duration", "s");
        analysisDuration = meter.CreateHistogram<double>("panko.case_analysis.duration", "s");
        llmCallsAvoided = meter.CreateCounter<long>("panko.case_analysis.llm_calls_avoided");
        mcpCommands = meter.CreateCounter<long>("panko.mcp.commands");
        mcpFailures = meter.CreateCounter<long>("panko.mcp.failures");
    }

    public void CaseCreated(string origin) =>
        casesCreated.Add(1, new KeyValuePair<string, object?>("origin", origin));

    public void CrumbsAccepted(int count) => crumbsAccepted.Add(count);

    public void CrumbsDeduplicated(int count) => crumbsDeduplicated.Add(count);

    public void CrumbRejected(string reason) =>
        crumbsRejected.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void ProjectionLag(long inputVersion, long projectedInputVersion) =>
        projectionLag.Record(Math.Max(0, inputVersion - projectedInputVersion));

    public void ProjectionCompleted(TimeSpan duration) => projectionDuration.Record(duration.TotalSeconds);

    public void ProjectionRetried() => projectionRetries.Add(1);

    public void SourceRefreshCompleted(TimeSpan duration) => sourceRefreshDuration.Record(duration.TotalSeconds);

    public void AnalysisCompleted(TimeSpan duration) => analysisDuration.Record(duration.TotalSeconds);

    public void LlmCallAvoided(string reason) => LlmCallsAvoided(1, reason);

    public void LlmCallsAvoided(long count, string reason)
    {
        if (count > 0)
        {
            llmCallsAvoided.Add(count, new KeyValuePair<string, object?>("reason", reason));
        }
    }

    public void McpCommand(string tool) =>
        mcpCommands.Add(1, new KeyValuePair<string, object?>("tool", tool));

    public void McpFailure(string tool) =>
        mcpFailures.Add(1, new KeyValuePair<string, object?>("tool", tool));
}
