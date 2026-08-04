using System.Text.Json.Nodes;
using System.Threading.Channels;
using Panko.Api.Domain;
using Panko.Api.Cases;

namespace Panko.Api.Demo;

internal sealed record DemoReplayTransition(
    CaseFile? CaseFile,
    CaseProgress? Progress);

public sealed class DemoCaseStore(TimeProvider timeProvider) :
    ICaseFileReader,
    ICaseProgressReader
{
    public static readonly Guid CaseId = Guid.Parse("11111111-1111-1111-1111-111111111111");

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
    private Guid _progressAttemptId;
    private long _progressRevision;
    private DateTimeOffset _baseTime = timeProvider.GetUtcNow();
    private CaseFile _caseFile = null!;
    private CaseProgress? _progress;

    internal (
        int Generation,
        CaseFile CaseFile,
        CaseProgress Progress) Reset()
    {
        lock (_gate)
        {
            _generation++;
            _version++;
            _progressAttemptId = Guid.NewGuid();
            _progressRevision = 1;
            _baseTime = timeProvider.GetUtcNow();
            _caseFile = BuildCaseFile(0, _version);
            _progress = BuildProgress(0, _progressRevision, _caseFile.CaseFileVersion);
            _starts.Writer.TryWrite(_generation);
            return (_generation, _caseFile, _progress);
        }
    }

    public CaseFile Get()
    {
        lock (_gate)
        {
            EnsureInitialized();
            return _caseFile;
        }
    }

    public Task<CaseFileState?> GetAsync(Guid caseId, CancellationToken cancellationToken) =>
        Task.FromResult(caseId == CaseId
            ? CaseFileState.From(Get()) with { Team = "payments" }
            : null);

    public Task<CaseProgress?> GetProgressAsync(
        Guid caseId,
        CancellationToken cancellationToken)
    {
        if (caseId != CaseId)
        {
            return Task.FromResult<CaseProgress?>(null);
        }

        lock (_gate)
        {
            EnsureInitialized();
            return Task.FromResult(_progress);
        }
    }

    internal DemoReplayTransition? Advance(int generation, int phase)
    {
        lock (_gate)
        {
            if (generation != _generation) return null;

            if (phase is < 1 or > 6)
            {
                throw new ArgumentOutOfRangeException(nameof(phase), phase, "Demo phase must be between 1 and 6.");
            }

            if (phase < 6)
            {
                _progressRevision++;
                _progress = BuildProgress(phase, _progressRevision, _caseFile.CaseFileVersion);
                return new DemoReplayTransition(null, _progress);
            }

            _version++;
            _caseFile = BuildCaseFile(phase, _version);
            _progress = null;
            return new DemoReplayTransition(_caseFile, null);
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

    private void EnsureInitialized()
    {
        if (_caseFile is not null) return;

        _version++;
        _progressAttemptId = Guid.NewGuid();
        _progressRevision = 1;
        _baseTime = timeProvider.GetUtcNow();
        _caseFile = BuildCaseFile(0, _version);
        _progress = BuildProgress(0, _progressRevision, _caseFile.CaseFileVersion);
    }

    private CaseFile BuildCaseFile(int phase, int version)
    {
        var codeReference = new CodeReference(
            "code-timeout-handler-43-44", "platform/payments", "abcdef1234567890",
            "src/Payments.Api/Handler.cs", 43, 44,
            "https://gitlab.example/platform/payments/-/blob/abcdef1234567890/src/Payments.Api/Handler.cs#L43-44",
            "+throw new TimeoutException(\"Authorisation exceeded 750ms\");\n+metrics.RecordTimeout();");
        var crumbs = new List<Crumb>();

        if (phase >= 1)
        {
            crumbs.Add(CreateCrumb("demo-mr-created", "gitlab", _baseTime - TimeSpan.FromMinutes(20),
                "merge-request-created", "info", "Alex Chen created MR !42: tighten payment timeout",
                "https://gitlab.example/platform/payments/-/merge_requests/42", "Alex Chen", "commit", "abcdef1234567890"));
        }
        if (phase >= 2)
        {
            crumbs.Add(CreateCrumb("demo-mr-merged", "gitlab", _baseTime - TimeSpan.FromMinutes(2),
                "merge-request-merged", "info", "Alex Chen merged MR !42: tighten payment timeout",
                "https://gitlab.example/platform/payments/-/merge_requests/42", "Alex Chen", "commit", "abcdef1234567890",
                [codeReference]));
        }
        if (phase >= 3)
        {
            crumbs.Add(CreateCrumb("demo-deployment", "gitlab", _baseTime - TimeSpan.FromSeconds(45),
                "deployment", "info", "GitLab CI deployed abcdef12 to production; deployment 918 is success",
                "https://gitlab.example/platform/payments/-/environments/7", "GitLab CI", "commit", "abcdef1234567890",
                [codeReference]));
        }
        if (phase >= 4)
        {
            crumbs.Add(CreateCrumb("demo-nomad-failure", "nomad", _baseTime + TimeSpan.FromSeconds(2),
                "workload-failure", "warning", "Nomad allocation 73af920c for payments-api failed its health check",
                "https://nomad.example/ui/jobs/payments-api", null, "nomad-allocation", "73af920c"));
            crumbs.Add(CreateCrumb("demo-latency", "grafana", _baseTime + TimeSpan.FromSeconds(3),
                "metric", "warning", "p99 authorisation latency rose from 420ms to 1.8s",
                "https://grafana.example/d/payments-overview?viewPanel=7", null, "grafana-panel", "payments-overview/7"));
        }
        if (phase >= 5)
        {
            crumbs.Add(CreateCrumb("demo-first-error", "victorialogs", _baseTime + TimeSpan.FromSeconds(5),
                "first-error", "warning", "First observed errors in the Case window: payment authorisation exceeded 750ms",
                "https://victorialogs.example/select/vmui", null, "log-query", "payments-timeouts"));
            crumbs.Add(CreateCrumb("demo-failed-pipeline", "gitlab", _baseTime + TimeSpan.FromSeconds(8),
                "pipeline", "warning", "Pipeline 919 on main is failed while preparing the rollback",
                "https://gitlab.example/platform/payments/-/pipelines/919", "GitLab CI", "pipeline", "919"));
        }

        var causalMarkers = CaseFileComposer.BuildCausalMarkers(crumbs);
        var trail = new List<TrailCandidate>
        {
            new(_baseTime, "pagerduty", "pagerduty-incident", "PagerDuty triggered: payment authorisations timing out", "critical",
                "https://pagerduty.example/incidents/PDEMO")
        };
        trail.AddRange(causalMarkers.Select(item => new TrailCandidate(
            item.OccurredAt, item.Source, item.Category, item.Summary,
            item.Category is "workload-failure" or "first-error" ? "warning" : "info",
            item.Url, item.Actor, item.ObjectType, item.ObjectId)));
        trail.AddRange(crumbs.Where(item => item.Category == "pipeline").Select(item => new TrailCandidate(
            item.OccurredAt, item.Source, item.Category, item.Summary, item.Severity,
            item.Url, item.Actor, item.ObjectType, item.ObjectId)));
        trail = trail.OrderBy(item => item.OccurredAt).ToList();

        var ai = phase >= 6
            ? new AiSynthesis(
                "complete",
                "The timeout change in MR !42 is temporally aligned with the production deployment and failure sequence, ending in the first timeout error. Rollback pipeline 919 then failed.",
                ["The new 750ms timeout is below the observed upstream response time."],
                ["The demo does not include upstream dependency traces."],
                ["Compare the new timeout with the upstream SLA before rollback."],
                "demo-crumbs-v1",
                [new AiDiagnosis(
                    "Handler.cs lines 43–44 introduce the 750ms exception recorded immediately after deployment.",
                    ["demo-mr-merged", "demo-deployment", "demo-first-error"], [codeReference],
                    1, 95)],
                [
                    new AiSummaryPart("The timeout change in "),
                    new AiSummaryPart("MR !42", "crumb:demo-mr-merged"),
                    new AiSummaryPart(" is temporally aligned with the "),
                    new AiSummaryPart("production deployment and failure sequence", "section:causal-sequence"),
                    new AiSummaryPart(", ending in the first "),
                    new AiSummaryPart("timeout error", "section:log-errors"),
                    new AiSummaryPart(". Rollback "),
                    new AiSummaryPart("pipeline 919", "crumb:demo-failed-pipeline"),
                    new AiSummaryPart(" then failed.")
                ],
                [
                    new AiSummaryReference("crumb:demo-mr-merged", "MR !42", "external", "https://gitlab.example/platform/payments/-/merge_requests/42"),
                    new AiSummaryReference("crumb:demo-failed-pipeline", "Pipeline 919", "external", "https://gitlab.example/platform/payments/-/pipelines/919"),
                    new AiSummaryReference("section:causal-sequence", "candidate causal sequence", "section", "#causal-sequence"),
                    new AiSummaryReference("section:log-errors", "summarised log errors", "section", "#log-errors")
                ])
            : new AiSynthesis("pending", null, [], [], [], null);

        var summary = phase switch
        {
            0 => "PagerDuty incident accepted. Starting scoped collectors.",
            1 => "Found a recent merge request for payments-api.",
            2 => "The merge request was merged shortly before the Case opened.",
            3 => "The merged commit was deployed to production.",
            4 => "The production deployment was followed by a Nomad failure and latency increase.",
            5 => "Candidate sequence: MR created → MR merged → production deployment → Nomad failure → first log error.",
            _ => "Cited diagnosis is ready. Candidate sequence remains a correlation, not proof of causation."
        };
        var sources = new List<CrumbSourceStatus>
        {
            Source("pagerduty", CrumbSourceHealth.Complete, 1, 24),
            Source(
                "gitlab",
                phase >= 3 ? CrumbSourceHealth.Complete : CrumbSourceHealth.Pending,
                crumbs.Count(item => item.Source == "gitlab"),
                phase >= 5 ? 244 : phase >= 3 ? 184 : 0),
            Source("nomad", phase >= 4 ? CrumbSourceHealth.Complete : CrumbSourceHealth.Pending, crumbs.Count(item => item.Source == "nomad"), phase >= 4 ? 96 : 0),
            Source("grafana", phase >= 4 ? CrumbSourceHealth.Complete : CrumbSourceHealth.Pending, crumbs.Count(item => item.Source == "grafana"), phase >= 4 ? 133 : 0),
            Source("victorialogs", phase >= 5 ? CrumbSourceHealth.Complete : CrumbSourceHealth.Pending, crumbs.Count(item => item.Source == "victorialogs"), phase >= 5 ? 211 : 0)
        };

        return new CaseFile(
            CaseId, "PDEMO", "payments-api", "payments-production", "demo-v1",
            "Payment authorisations timing out", "high", PagerDutyIncidentState.Triggered,
            phase >= 6 ? CaseProgression.Ready : CaseProgression.Collecting,
            _baseTime, timeProvider.GetUtcNow(), version,
            summary, ai, trail, crumbs.OrderByDescending(item => item.OccurredAt).ToList(), sources,
            [
                new SourceLink("PagerDuty incident", "https://pagerduty.example/incidents/PDEMO"),
                new SourceLink("Payments dashboard", "https://grafana.example/d/payments-overview"),
                new SourceLink("GitLab MR !42", "https://gitlab.example/platform/payments/-/merge_requests/42"),
                new SourceLink("Failed pipeline 919", "https://gitlab.example/platform/payments/-/pipelines/919"),
                new SourceLink("Nomad payments-api", "https://nomad.example/ui/jobs/payments-api"),
                new SourceLink("VictoriaLogs errors", "https://victorialogs.example/select/vmui")
            ], causalMarkers, BuildPattern(phase));
    }

    private CaseProgress BuildProgress(
        int phase,
        long revision,
        int baseCaseFileVersion)
    {
        var now = timeProvider.GetUtcNow();
        var deterministicUsable = phase >= 5;
        var pass = phase >= 4 ? 2 : 1;
        var lookbackMinutes = pass == 2 ? 120 : 30;
        var progressSources = new[]
        {
            ProgressSource(
                "pagerduty", CrumbSourceProgressState.Received, CrumbSourceHealth.Complete,
                1, 30, 24, 1, now),
            phase == 0
                ? ProgressSource(
                    "gitlab", CrumbSourceProgressState.Querying, CrumbSourceHealth.Pending,
                    1, 30, 0, 0, now)
                : ProgressSource(
                    "gitlab", CrumbSourceProgressState.Received, CrumbSourceHealth.Complete,
                    phase >= 5 ? 2 : 1,
                    phase >= 5 ? 120 : 30,
                    phase >= 5 ? 244 : 184,
                    phase >= 5 ? 4 : 3,
                    now),
            phase switch
            {
                0 => ProgressSource(
                    "nomad", CrumbSourceProgressState.Pending, CrumbSourceHealth.Pending,
                    0, 30, 0, 0, now),
                1 => ProgressSource(
                    "nomad", CrumbSourceProgressState.Querying, CrumbSourceHealth.Pending,
                    1, 30, 0, 0, now),
                _ => ProgressSource(
                    "nomad", CrumbSourceProgressState.Received, CrumbSourceHealth.Complete,
                    1, 30, 96, 1, now)
            },
            phase switch
            {
                < 2 => ProgressSource(
                    "grafana", CrumbSourceProgressState.Pending, CrumbSourceHealth.Pending,
                    0, 30, 0, 0, now),
                2 => ProgressSource(
                    "grafana", CrumbSourceProgressState.Querying, CrumbSourceHealth.Pending,
                    1, 30, 0, 0, now),
                _ => ProgressSource(
                    "grafana", CrumbSourceProgressState.Received, CrumbSourceHealth.Complete,
                    1, 30, 133, 1, now)
            },
            phase switch
            {
                < 4 => ProgressSource(
                    "victorialogs", CrumbSourceProgressState.Pending, CrumbSourceHealth.Pending,
                    0, 30, 0, 0, now),
                4 => ProgressSource(
                    "victorialogs", CrumbSourceProgressState.Querying, CrumbSourceHealth.Pending,
                    2, 120, 0, 0, now),
                _ => ProgressSource(
                    "victorialogs", CrumbSourceProgressState.Received, CrumbSourceHealth.Complete,
                    2, 120, 211, 1, now)
            }
        };
        return new CaseProgress(
            CaseId,
            _progressAttemptId,
            revision,
            baseCaseFileVersion,
            _baseTime,
            now,
            Math.Max(0, (long)(now - _baseTime).TotalMilliseconds),
            deterministicUsable
                ? CaseProgressPhase.Synthesizing
                : CaseProgressPhase.Collecting,
            pass,
            lookbackMinutes,
            deterministicUsable,
            deterministicUsable,
            deterministicUsable
                ? AiSynthesisProgressState.Running
                : AiSynthesisProgressState.Pending,
            progressSources,
            BuildEarlyCrumbs(phase));
    }

    private CaseSourceProgress ProgressSource(
        string source,
        CrumbSourceProgressState requestState,
        CrumbSourceHealth health,
        int pass,
        int lookbackMinutes,
        long durationMilliseconds,
        int crumbCount,
        DateTimeOffset now) => new(
            source,
            requestState,
            health,
            pass,
            lookbackMinutes,
            durationMilliseconds,
            crumbCount,
            null,
            requestState == CrumbSourceProgressState.Pending ? null : _baseTime,
            requestState == CrumbSourceProgressState.Pending ? _baseTime : now);

    private IReadOnlyList<CaseEarlyCrumb> BuildEarlyCrumbs(int phase)
    {
        var crumbs = new List<CaseEarlyCrumb>();
        if (phase >= 5)
        {
            crumbs.Add(new CaseEarlyCrumb(
                "demo-first-error", "victorialogs", _baseTime + TimeSpan.FromSeconds(5),
                "warning", "First observed errors in the Case window: payment authorisation exceeded 750ms", .95));
        }
        if (phase >= 2)
        {
            crumbs.Add(new CaseEarlyCrumb(
                "demo-nomad-failure", "nomad", _baseTime + TimeSpan.FromSeconds(2),
                "warning", "Nomad allocation 73af920c for payments-api failed its health check", .95));
        }
        if (phase >= 1)
        {
            crumbs.Add(new CaseEarlyCrumb(
                "demo-deployment", "gitlab", _baseTime - TimeSpan.FromSeconds(45),
                "info", "GitLab CI deployed abcdef12 to production; deployment 918 is success", .95));
            crumbs.Add(new CaseEarlyCrumb(
                "demo-mr-merged", "gitlab", _baseTime - TimeSpan.FromMinutes(2),
                "info", "Alex Chen merged MR !42: tighten payment timeout", .95));
        }
        if (phase >= 3)
        {
            crumbs.Add(new CaseEarlyCrumb(
                "demo-latency", "grafana", _baseTime + TimeSpan.FromSeconds(3),
                "warning", "p99 authorisation latency rose from 420ms to 1.8s", .95));
        }
        if (phase >= 1)
        {
            crumbs.Add(new CaseEarlyCrumb(
                "demo-mr-created", "gitlab", _baseTime - TimeSpan.FromMinutes(20),
                "info", "Alex Chen created MR !42: tighten payment timeout", .95));
        }
        return crumbs.Take(5).ToArray();
    }

    private PatternContext BuildPattern(int phase)
    {
        if (phase == 0)
        {
            return new PatternContext("provisional", "v1", SignatureStage.Provisional, null, null, null,
                null, null, [], 0, null, null, [], [], 0.35);
        }
        var history = new[]
        {
            new PatternOccurrenceSummary(Guid.Parse("22222222-2222-2222-2222-222222222222"), "PD-OLD-1", PagerDutyIncidentState.Resolved,
                _baseTime - TimeSpan.FromDays(24), _baseTime - TimeSpan.FromDays(23), null),
            new PatternOccurrenceSummary(Guid.Parse("33333333-3333-3333-3333-333333333333"), "PD-OLD-2", PagerDutyIncidentState.Resolved,
                _baseTime - TimeSpan.FromDays(12), _baseTime - TimeSpan.FromDays(11), null),
            new PatternOccurrenceSummary(Guid.Parse("44444444-4444-4444-4444-444444444444"), "PD-OLD-3", PagerDutyIncidentState.Resolved,
                _baseTime - TimeSpan.FromDays(4), _baseTime - TimeSpan.FromDays(3), null),
            new PatternOccurrenceSummary(CaseId, "PDEMO", PagerDutyIncidentState.Triggered, _baseTime, timeProvider.GetUtcNow(), $"/cases/{CaseId}")
        };
        return new PatternContext("available", "v1", SignatureStage.Final, "PAYMENTS-CHECKOUT-4F19",
            Guid.Parse("55555555-5555-5555-5555-555555555555"), PatternLifecycleState.Regressed,
            "similarity", 90, ["error: payment authorisation timeout", "component: payments-api", "code location: platform/payments:src/payments.api/handler.cs"],
            4, history[0].OccurredAt, _baseTime, history.Reverse().ToArray(), [], 0.9);
    }

    private static Crumb CreateCrumb(
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

    private static CrumbSourceStatus Source(string source, CrumbSourceHealth health, int count, long duration) =>
        new(source, health, count, duration, null, [], health switch
        {
            CrumbSourceHealth.Pending => CrumbSourceRequestState.Requested,
            CrumbSourceHealth.Unavailable => CrumbSourceRequestState.Errored,
            _ => CrumbSourceRequestState.Received
        });
}
