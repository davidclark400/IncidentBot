using System.Text.Json.Nodes;
using System.Threading.Channels;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Incidents;

namespace IncidentBot.Api.Demo;

public sealed class DemoIncidentStore(TimeProvider timeProvider) : IIncidentReportReader
{
    public static readonly Guid IncidentId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly object _gate = new();
    private readonly Channel<int> _starts = Channel.CreateBounded<int>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private int _generation;
    private int _version;
    private DateTimeOffset _baseTime = timeProvider.GetUtcNow();
    private InvestigationReport _report = null!;

    internal (int Generation, InvestigationReport Report) Reset()
    {
        lock (_gate)
        {
            _generation++;
            _version++;
            _baseTime = timeProvider.GetUtcNow();
            _report = BuildReport(0, _version);
            _starts.Writer.TryWrite(_generation);
            return (_generation, _report);
        }
    }

    public InvestigationReport Get()
    {
        lock (_gate)
        {
            if (_report is null)
            {
                _version++;
                _report = BuildReport(0, _version);
            }
            return _report;
        }
    }

    public Task<IncidentReportState?> GetAsync(Guid incidentId, CancellationToken cancellationToken) =>
        Task.FromResult(incidentId == IncidentId
            ? IncidentReportState.From(Get())
            : null);

    internal InvestigationReport? Advance(int generation, int phase)
    {
        lock (_gate)
        {
            if (generation != _generation) return null;
            _version++;
            _report = BuildReport(phase, _version);
            return _report;
        }
    }

    internal bool IsCurrentGeneration(int generation)
    {
        lock (_gate)
        {
            return generation == _generation;
        }
    }

    internal IAsyncEnumerable<int> ReadStartsAsync(CancellationToken cancellationToken) =>
        _starts.Reader.ReadAllAsync(cancellationToken);

    private InvestigationReport BuildReport(int phase, int version)
    {
        var codeReference = new CodeReference(
            "code-timeout-handler-43-44", "platform/payments", "abcdef1234567890",
            "src/Payments.Api/Handler.cs", 43, 44,
            "https://gitlab.example/platform/payments/-/blob/abcdef1234567890/src/Payments.Api/Handler.cs#L43-44",
            "+throw new TimeoutException(\"Authorisation exceeded 750ms\");\n+metrics.RecordTimeout();");
        var evidence = new List<EvidenceFinding>();

        if (phase >= 1)
        {
            evidence.Add(Finding("demo-mr-created", "gitlab", _baseTime - TimeSpan.FromMinutes(20),
                "merge-request-created", "info", "Alex Chen created MR !42: tighten payment timeout",
                "https://gitlab.example/platform/payments/-/merge_requests/42", "Alex Chen", "commit", "abcdef1234567890"));
        }
        if (phase >= 2)
        {
            evidence.Add(Finding("demo-mr-merged", "gitlab", _baseTime - TimeSpan.FromMinutes(2),
                "merge-request-merged", "info", "Alex Chen merged MR !42: tighten payment timeout",
                "https://gitlab.example/platform/payments/-/merge_requests/42", "Alex Chen", "commit", "abcdef1234567890",
                [codeReference]));
        }
        if (phase >= 3)
        {
            evidence.Add(Finding("demo-deployment", "gitlab", _baseTime - TimeSpan.FromSeconds(45),
                "deployment", "info", "GitLab CI deployed abcdef12 to production; deployment 918 is success",
                "https://gitlab.example/platform/payments/-/environments/7", "GitLab CI", "commit", "abcdef1234567890",
                [codeReference]));
        }
        if (phase >= 4)
        {
            evidence.Add(Finding("demo-nomad-failure", "nomad", _baseTime + TimeSpan.FromSeconds(2),
                "workload-failure", "warning", "Nomad allocation 73af920c for payments-api failed its health check",
                "https://nomad.example/ui/jobs/payments-api", null, "nomad-allocation", "73af920c"));
            evidence.Add(Finding("demo-latency", "grafana", _baseTime + TimeSpan.FromSeconds(3),
                "metric", "warning", "p99 authorisation latency rose from 420ms to 1.8s",
                "https://grafana.example/d/payments-overview?viewPanel=7", null, "grafana-panel", "payments-overview/7"));
        }
        if (phase >= 5)
        {
            evidence.Add(Finding("demo-first-error", "victorialogs", _baseTime + TimeSpan.FromSeconds(5),
                "first-error", "warning", "First observed Errors in the investigation window: payment authorisation exceeded 750ms",
                "https://victorialogs.example/select/vmui", null, "log-query", "payments-timeouts"));
            evidence.Add(Finding("demo-failed-pipeline", "gitlab", _baseTime + TimeSpan.FromSeconds(8),
                "pipeline", "warning", "Pipeline 919 on main is failed while preparing the rollback",
                "https://gitlab.example/platform/payments/-/pipelines/919", "GitLab CI", "pipeline", "919"));
        }

        var causalEvents = ReportComposer.BuildCausalEvents(evidence);
        var timeline = new List<TimelineCandidate>
        {
            new(_baseTime, "pagerduty", "incident", "PagerDuty triggered: payment authorisations timing out", "critical",
                "https://pagerduty.example/incidents/PDEMO")
        };
        timeline.AddRange(causalEvents.Select(item => new TimelineCandidate(
            item.OccurredAt, item.Source, item.Category, item.Summary,
            item.Category is "workload-failure" or "first-error" ? "warning" : "info",
            item.Url, item.Actor, item.ObjectType, item.ObjectId)));
        timeline.AddRange(evidence.Where(item => item.Category == "pipeline").Select(item => new TimelineCandidate(
            item.OccurredAt, item.Source, item.Category, item.Summary, item.Severity,
            item.Url, item.Actor, item.ObjectType, item.ObjectId)));
        timeline = timeline.OrderBy(item => item.OccurredAt).ToList();

        var ai = phase >= 6
            ? new AiSynthesis(
                "complete",
                "The timeout change in MR !42 is temporally aligned with the production deployment and failure sequence, ending in the first timeout error. Rollback pipeline 919 then failed.",
                ["The new 750ms timeout is below the observed upstream response time."],
                ["The demo does not include upstream dependency traces."],
                ["Compare the new timeout with the upstream SLA before rollback."],
                "demo-evidence-v1",
                [new AiDiagnosis(
                    "Handler.cs lines 43–44 introduce the 750ms exception recorded immediately after deployment.",
                    ["demo-mr-merged", "demo-deployment", "demo-first-error"], [codeReference],
                    1, 95)],
                [
                    new AiSummaryPart("The timeout change in "),
                    new AiSummaryPart("MR !42", "evidence:demo-mr-merged"),
                    new AiSummaryPart(" is temporally aligned with the "),
                    new AiSummaryPart("production deployment and failure sequence", "section:causal-sequence"),
                    new AiSummaryPart(", ending in the first "),
                    new AiSummaryPart("timeout error", "section:log-errors"),
                    new AiSummaryPart(". Rollback "),
                    new AiSummaryPart("pipeline 919", "evidence:demo-failed-pipeline"),
                    new AiSummaryPart(" then failed.")
                ],
                [
                    new AiSummaryReference("evidence:demo-mr-merged", "MR !42", "external", "https://gitlab.example/platform/payments/-/merge_requests/42"),
                    new AiSummaryReference("evidence:demo-failed-pipeline", "Pipeline 919", "external", "https://gitlab.example/platform/payments/-/pipelines/919"),
                    new AiSummaryReference("section:causal-sequence", "candidate causal sequence", "section", "#causal-sequence"),
                    new AiSummaryReference("section:log-errors", "summarised log errors", "section", "#log-errors")
                ])
            : new AiSynthesis("pending", null, [], [], [], null);

        var summary = phase switch
        {
            0 => "PagerDuty incident accepted. Starting scoped collectors.",
            1 => "Found a recent merge request for payments-api.",
            2 => "The merge request was merged shortly before the incident.",
            3 => "The merged commit was deployed to production.",
            4 => "The production deployment was followed by a Nomad failure and latency increase.",
            5 => "Candidate sequence: MR created → MR merged → production deployment → Nomad failure → first log error.",
            _ => "Cited diagnosis is ready. Candidate sequence remains a correlation, not proof of causation."
        };
        var sources = new List<SourceReport>
        {
            Source("pagerduty", SourceHealth.Complete, 1, 24),
            Source("gitlab", phase >= 3 ? SourceHealth.Complete : SourceHealth.Pending, evidence.Count(item => item.Source == "gitlab"), phase >= 3 ? 184 : 0),
            Source("nomad", phase >= 4 ? SourceHealth.Complete : SourceHealth.Pending, evidence.Count(item => item.Source == "nomad"), phase >= 4 ? 96 : 0),
            Source("grafana", phase >= 4 ? SourceHealth.Complete : SourceHealth.Pending, evidence.Count(item => item.Source == "grafana"), phase >= 4 ? 133 : 0),
            Source("victorialogs", phase >= 5 ? SourceHealth.Complete : SourceHealth.Pending, evidence.Count(item => item.Source == "victorialogs"), phase >= 5 ? 211 : 0)
        };

        return new InvestigationReport(
            IncidentId, "PDEMO", "payments-api", "payments-production", "demo-v1",
            "Payment authorisations timing out", "high", IncidentState.Triggered,
            phase >= 6 ? IncidentProgression.Ready : IncidentProgression.Collecting,
            _baseTime, timeProvider.GetUtcNow(), version,
            summary, ai, timeline, evidence.OrderByDescending(item => item.OccurredAt).ToList(), sources,
            [
                new SourceLink("PagerDuty incident", "https://pagerduty.example/incidents/PDEMO"),
                new SourceLink("Payments dashboard", "https://grafana.example/d/payments-overview"),
                new SourceLink("GitLab MR !42", "https://gitlab.example/platform/payments/-/merge_requests/42"),
                new SourceLink("Failed pipeline 919", "https://gitlab.example/platform/payments/-/pipelines/919"),
                new SourceLink("Nomad payments-api", "https://nomad.example/ui/jobs/payments-api"),
                new SourceLink("VictoriaLogs errors", "https://victorialogs.example/select/vmui")
            ], causalEvents, BuildProblem(phase));
    }

    private ProblemContext BuildProblem(int phase)
    {
        if (phase == 0)
        {
            return new ProblemContext("provisional", "v1", FingerprintStage.Provisional, null, null, null,
                null, null, [], 0, null, null, [], [], 0.35);
        }
        var history = new[]
        {
            new ProblemOccurrenceSummary(Guid.Parse("22222222-2222-2222-2222-222222222222"), "PD-OLD-1", IncidentState.Resolved,
                _baseTime - TimeSpan.FromDays(24), _baseTime - TimeSpan.FromDays(23), null),
            new ProblemOccurrenceSummary(Guid.Parse("33333333-3333-3333-3333-333333333333"), "PD-OLD-2", IncidentState.Resolved,
                _baseTime - TimeSpan.FromDays(12), _baseTime - TimeSpan.FromDays(11), null),
            new ProblemOccurrenceSummary(Guid.Parse("44444444-4444-4444-4444-444444444444"), "PD-OLD-3", IncidentState.Resolved,
                _baseTime - TimeSpan.FromDays(4), _baseTime - TimeSpan.FromDays(3), null),
            new ProblemOccurrenceSummary(IncidentId, "PDEMO", IncidentState.Triggered, _baseTime, timeProvider.GetUtcNow(), $"/incidents/{IncidentId}")
        };
        return new ProblemContext("available", "v1", FingerprintStage.Final, "PAYMENTS-CHECKOUT-4F19",
            Guid.Parse("55555555-5555-5555-5555-555555555555"), ProblemLifecycleState.Regressed,
            "similarity", 90, ["error: payment authorisation timeout", "component: payments-api", "code location: platform/payments:src/payments.api/handler.cs"],
            4, history[0].OccurredAt, _baseTime, history.Reverse().ToArray(), [], 0.9);
    }

    private static EvidenceFinding Finding(
        string id,
        string source,
        DateTimeOffset occurredAt,
        string category,
        string severity,
        string summary,
        string url,
        string? actor,
        string objectType,
        string objectId,
        IReadOnlyList<CodeReference>? codeReferences = null) =>
        new(id, source, occurredAt, null, category, severity, summary, null, url, 0.95,
            new JsonObject { ["mode"] = "demo", ["fixture"] = id }, actor, objectType, objectId, codeReferences);

    private static SourceReport Source(string source, SourceHealth health, int count, long duration) =>
        new(source, health, count, duration, null, [], health switch
        {
            SourceHealth.Pending => SourceRequestState.Requested,
            SourceHealth.Unavailable => SourceRequestState.Errored,
            _ => SourceRequestState.Received
        });
}
