using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentBot.Api.Connectors;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Incidents;
using IncidentBot.Api.Options;
using IncidentBot.Api.Security;
using IncidentBot.Kafka;
using Microsoft.Extensions.Logging.Abstractions;

namespace IncidentBot.Api.Tests;

public sealed class SlackMentionTransportTests
{
    [Fact]
    public async Task EligibleEnvelopeIsNormalizedAndQueued()
    {
        var admission = Admission();

        var result = Admit(admission, Envelope(
            text: "<@UBOT> show <@UOTHER> latency &amp; errors &lt; 1s",
            threadTimestamp: "1710000000.000001"));
        var mention = await ReadAcceptedAsync(admission);

        Assert.Equal(SlackPromptAdmissionOutcome.Accepted, result.Outcome);
        Assert.Null(result.BusyMention);
        Assert.Equal("Ev123", mention.EventId);
        Assert.Equal("C123", mention.ChannelId);
        Assert.Equal("1710000000.000001", mention.ThreadTimestamp);
        Assert.Equal("show <@UOTHER> latency & errors < 1s", mention.Prompt);
    }

    [Fact]
    public async Task DuplicateEventsAreDroppedAndOldEntriesAreEvictedAtTheFiniteCapacity()
    {
        var admission = Admission(queueCapacity: 1, perUserRateLimit: 60, globalRateLimit: 600);
        var first = Envelope(eventId: "event-0", user: "U0");

        Assert.Equal(SlackPromptAdmissionOutcome.Accepted, Admit(admission, first).Outcome);
        await ReadAcceptedAsync(admission);
        Assert.Equal(SlackPromptAdmissionOutcome.Dropped, Admit(admission, first).Outcome);

        for (var index = 1; index <= 64; index++)
        {
            Assert.Equal(
                SlackPromptAdmissionOutcome.Accepted,
                Admit(admission, Envelope(eventId: $"event-{index}", user: $"U{index}")).Outcome);
            await ReadAcceptedAsync(admission);
        }

        Assert.Equal(SlackPromptAdmissionOutcome.Accepted, Admit(admission, first).Outcome);
        await ReadAcceptedAsync(admission);
    }

    [Fact]
    public async Task UnauthorizedMalformedExternalBotSelfAndOversizedEventsAreDropped()
    {
        var admission = Admission(maximumPromptCharacters: 3);

        Assert.Equal(SlackPromptAdmissionOutcome.Dropped,
            Admit(admission, Envelope(), new SlackBotIdentity("T999", "UBOT")).Outcome);
        Assert.Equal(SlackPromptAdmissionOutcome.Dropped,
            Admit(admission, Envelope(payloadExtra: ",\"is_ext_shared_channel\":true")).Outcome);
        Assert.Equal(SlackPromptAdmissionOutcome.Dropped,
            Admit(admission, Envelope(eventExtra: ",\"bot_id\":\"B123\"")).Outcome);
        Assert.Equal(SlackPromptAdmissionOutcome.Dropped,
            Admit(admission, Envelope(user: "UBOT")).Outcome);
        Assert.Equal(SlackPromptAdmissionOutcome.Dropped,
            Admit(admission, Envelope(text: "<@UBOT> too long")).Outcome);
        Assert.Equal(SlackPromptAdmissionOutcome.Dropped,
            Admit(admission, """{ "type": "events_api", "payload": {} }""").Outcome);

        var externalAdmission = Admission(allowExternalSharedChannels: true);
        Assert.Equal(
            SlackPromptAdmissionOutcome.Accepted,
            Admit(externalAdmission, Envelope(
                payloadExtra: ",\"is_ext_shared_channel\":true")).Outcome);
        await ReadAcceptedAsync(externalAdmission);
    }

    [Fact]
    public async Task PerUserAndGlobalRateRejectionsReturnBusyAndBudgetsResetAfterOneMinute()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-13T10:00:00Z"));
        var admission = Admission(
            queueCapacity: 4,
            perUserRateLimit: 2,
            globalRateLimit: 3,
            timeProvider: time);

        await AssertAcceptedAsync(admission, Envelope(eventId: "event-1", user: "U1"));
        await AssertAcceptedAsync(admission, Envelope(eventId: "event-2", user: "U1"));
        var perUserBusy = Admit(admission, Envelope(eventId: "event-3", user: "U1"));
        Assert.Equal(SlackPromptAdmissionOutcome.Busy, perUserBusy.Outcome);
        Assert.Equal("event-3", perUserBusy.BusyMention!.EventId);

        await AssertAcceptedAsync(admission, Envelope(eventId: "event-4", user: "U2"));
        var globalBusy = Admit(admission, Envelope(eventId: "event-5", user: "U3"));
        Assert.Equal(SlackPromptAdmissionOutcome.Busy, globalBusy.Outcome);
        Assert.Equal("event-5", globalBusy.BusyMention!.EventId);

        time.Advance(TimeSpan.FromMinutes(1));
        await AssertAcceptedAsync(admission, Envelope(eventId: "event-6", user: "U1"));
    }

    [Fact]
    public void FullAcceptedQueueReturnsBusyWithoutBlockingTheCaller()
    {
        var admission = Admission(queueCapacity: 1);

        Assert.Equal(
            SlackPromptAdmissionOutcome.Accepted,
            Admit(admission, Envelope(eventId: "event-1")).Outcome);
        var busy = Admit(admission, Envelope(eventId: "event-2", user: "U2"));

        Assert.Equal(SlackPromptAdmissionOutcome.Busy, busy.Outcome);
        Assert.Equal("event-2", busy.BusyMention!.EventId);
    }

    [Fact]
    public async Task ReplyPublisherUsesDocumentedThreadedJsonShapeAndEscapesControlText()
    {
        var handler = new CaptureHandler();
        var publisher = new SlackReplyPublisher(
            new HttpClientFactory(handler),
            Microsoft.Extensions.Options.Options.Create(new SlackOptions
            {
                Enabled = true,
                ApiBaseUrl = "https://slack.test/api",
                BotTokenEnv = "SLACK_TEST_TOKEN",
                TimeoutSeconds = 5
            }),
            TestConfiguration.Credentials(("SLACK_TEST_TOKEN", "xoxb-test")));

        await publisher.ReplyAsync(
            new SlackReplyTarget("C123", "1710000000.000001"),
            "Result <@U123> & details",
            CancellationToken.None);

        Assert.Equal("https://slack.test/api/chat.postMessage", handler.Uri?.ToString());
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("xoxb-test", handler.AuthorizationParameter);
        using var body = JsonDocument.Parse(Assert.IsType<string>(handler.Body));
        var root = body.RootElement;
        Assert.Equal("C123", root.GetProperty("channel").GetString());
        Assert.Equal("1710000000.000001", root.GetProperty("thread_ts").GetString());
        Assert.Equal("Result &lt;@U123&gt; &amp; details", root.GetProperty("text").GetString());
        Assert.False(root.GetProperty("mrkdwn").GetBoolean());
        Assert.Equal("none", root.GetProperty("parse").GetString());
        Assert.False(root.GetProperty("reply_broadcast").GetBoolean());
        Assert.False(root.GetProperty("unfurl_links").GetBoolean());
        Assert.False(root.GetProperty("unfurl_media").GetBoolean());
    }

    private static SlackPromptAdmission Admission(
        int queueCapacity = 8,
        int perUserRateLimit = 6,
        int globalRateLimit = 30,
        int maximumPromptCharacters = 2000,
        bool allowExternalSharedChannels = false,
        TimeProvider? timeProvider = null) => new(
        new SlackOptions
        {
            MaximumPromptCharacters = maximumPromptCharacters,
            PromptQueueCapacity = queueCapacity,
            PromptWorkerCount = 1,
            PromptRequestsPerMinutePerUser = perUserRateLimit,
            PromptRequestsPerMinute = globalRateLimit,
            AllowExternalSharedChannels = allowExternalSharedChannels
        },
        timeProvider ?? TimeProvider.System);

    private static SlackPromptAdmissionResult Admit(
        SlackPromptAdmission admission,
        string json,
        SlackBotIdentity? identity = null)
    {
        using var document = JsonDocument.Parse(json);
        return admission.Admit(
            document.RootElement,
            identity ?? new SlackBotIdentity("T123", "UBOT"));
    }

    private static async Task AssertAcceptedAsync(
        SlackPromptAdmission admission,
        string envelope)
    {
        Assert.Equal(SlackPromptAdmissionOutcome.Accepted, Admit(admission, envelope).Outcome);
        await ReadAcceptedAsync(admission);
    }

    private static async Task<SlackMention> ReadAcceptedAsync(SlackPromptAdmission admission)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await using var reader = admission.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        return reader.Current;
    }

    private static string Envelope(
        string eventId = "Ev123",
        string text = "<@UBOT> investigate payments",
        string user = "U123",
        string? threadTimestamp = null,
        string payloadExtra = "",
        string eventExtra = "") => $$"""
        {
          "type": "events_api",
          "envelope_id": "env-123",
          "payload": {
            "type": "event_callback",
            "team_id": "T123",
            "event_id": "{{eventId}}"{{payloadExtra}},
            "event": {
              "type": "app_mention",
              "user": "{{user}}",
              "text": {{JsonSerializer.Serialize(text)}},
              "channel": "C123",
              "ts": "1710000001.000002"{{(threadTimestamp is null ? "" : $",\"thread_ts\":\"{threadTimestamp}\"")}}{{eventExtra}}
            }
          }
        }
        """;

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? Body { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true,\"ts\":\"1710000002.000003\"}", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class HttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}

public sealed class SlackQueryPlanningTests
{
    [Fact]
    public void CompilerNarrowsReviewedQueriesAndEmitsCanonicalYaml()
    {
        var compiled = new SlackQueryPlanCompiler(new SafeTemplateRenderer()).Compile(
            ValidPlan(),
            Profile());

        Assert.Null(compiled.Profile.PagerDuty);
        Assert.Null(compiled.Profile.VictoriaLogs);
        var query = Assert.Single(compiled.Profile.Grafana!.Queries);
        Assert.Equal("HTTP 5xx rate", query.Name);
        Assert.Empty(compiled.Profile.Grafana.Dashboards);
        Assert.Empty(compiled.Profile.Grafana.AnnotationTags);
        Assert.Contains("profileId: payments-production", compiled.AuditYaml, StringComparison.Ordinal);
        Assert.Contains("queryNames:", compiled.AuditYaml, StringComparison.Ordinal);
        Assert.Contains("HTTP 5xx rate", compiled.AuditYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("prometheus-production", compiled.AuditYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("sum(rate", compiled.AuditYaml, StringComparison.Ordinal);
    }

    [Fact]
    public void CompilerFailsClosedForUnknownQueriesUnsafeLabelsAndEmptyQuerySelection()
    {
        var compiler = new SlackQueryPlanCompiler(new SafeTemplateRenderer());
        var profile = Profile();

        Assert.Throws<InvalidOperationException>(() => compiler.Compile(
            ValidPlan(queryNames: ["not-reviewed"]), profile));
        Assert.Throws<InvalidOperationException>(() => compiler.Compile(
            ValidPlan(labels: [new SlackQueryLabel("environment", "production\" OR 1=1")]), profile));
        Assert.Throws<InvalidOperationException>(() => compiler.Compile(
            ValidPlan(labels: [new SlackQueryLabel("environment", "staging")]), profile));
        Assert.Throws<InvalidOperationException>(() => compiler.Compile(
            ValidPlan(queryNames: []), profile));
        Assert.Throws<InvalidOperationException>(() => compiler.Compile(
            ValidPlan(source: "pagerduty", queryNames: []), profile));
    }

    [Fact]
    public void KafkaSelectionKeepsReviewedScopeButLeaksNoPackOrPromQlIntoPlanCatalog()
    {
        var reviewed = Profile().WithKafka(new KafkaProfileScope
        {
            MetricPackId = "reviewed-exporter-v1",
            Cluster = "prod-cluster",
            Topics = ["payments"],
            ConsumerGroups = ["payments-workers"]
        });
        var compiled = new SlackQueryPlanCompiler(new SafeTemplateRenderer()).Compile(
            ValidPlan(source: "kafka", queryNames: []),
            reviewed);
        var kafkaCatalog = Assert.Single(
            LiteLlmSlackQueryPlanner.BuildSafeCatalog(reviewed),
            item => item.Source == "kafka");

        Assert.NotNull(compiled.Profile.Kafka);
        Assert.Equal(["payments"], compiled.Profile.Kafka.Topics);
        Assert.Null(compiled.Profile.Grafana);
        Assert.Null(compiled.Profile.VictoriaLogs);
        Assert.Empty(kafkaCatalog.QueryNames);
        Assert.Contains("source: kafka", compiled.AuditYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("reviewed-exporter-v1", compiled.AuditYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("prod-cluster", compiled.AuditYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("payments-workers", compiled.AuditYaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlannerUsesDedicatedModelAndSendsOnlySafeCatalogNames()
    {
        var planJson = JsonSerializer.Serialize(ValidPlan());
        var envelope = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = planJson } } }
        });
        var handler = new PlannerHandler(envelope);
        var planner = new LiteLlmSlackQueryPlanner(
            new HttpClientFactory(handler),
            Microsoft.Extensions.Options.Options.Create(new LiteLlmOptions
            {
                BaseUrl = "https://litellm.test",
                Model = "summary-model",
                QueryPlannerModel = "query-planner-model",
                ApiKeyEnv = "LITELLM_TEST_KEY",
                TimeoutSeconds = 5,
                InputCharacterBudget = 24000,
                MaxOutputTokens = 1000
            }),
            TestConfiguration.Credentials(("LITELLM_TEST_KEY", "secret")),
            NullLogger<LiteLlmSlackQueryPlanner>.Instance);

        var result = await planner.PlanAsync("Are errors rising?", Profile(), CancellationToken.None);

        Assert.Equal("Are errors rising?", result.Question);
        using var body = JsonDocument.Parse(Assert.IsType<string>(handler.Body));
        Assert.Equal("query-planner-model", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("json_schema", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        var serializedRequest = body.RootElement.ToString();
        Assert.Contains("HTTP 5xx rate", serializedRequest, StringComparison.Ordinal);
        Assert.DoesNotContain("prometheus-production", serializedRequest, StringComparison.Ordinal);
        Assert.DoesNotContain("sum(rate", serializedRequest, StringComparison.Ordinal);
        Assert.DoesNotContain("payments-production-stream", serializedRequest, StringComparison.Ordinal);
    }

    internal static SlackQueryPlan ValidPlan(
        IReadOnlyList<string>? queryNames = null,
        IReadOnlyList<SlackQueryLabel>? labels = null,
        string source = "grafana") => new(
        1,
        "Are errors rising?",
        labels ?? [new SlackQueryLabel("environment", "production")],
        [new SlackQuerySourceSelection(source, queryNames ?? ["HTTP 5xx rate"])]);

    internal static InvestigationProfile Profile() => new()
    {
        Id = "payments-production",
        PagerDutyServiceId = "P123PAYMENTS",
        Team = "payments",
        SlackChannel = "#payments-incidents",
        SlackPromptLabels = new Dictionary<string, string>
        {
            ["environment"] = "production"
        },
        PagerDuty = new PagerDutyScope(),
        Grafana = new GrafanaScope
        {
            OrganizationId = 1,
            Queries =
            [
                new GrafanaQuery
                {
                    Name = "HTTP 5xx rate",
                    DatasourceUid = "prometheus-production",
                    Expression = "sum(rate(http_requests_total{environment=\"{{environment}}\"}[5m]))",
                    WarningAbove = 1
                },
                new GrafanaQuery
                {
                    Name = "p99 latency",
                    DatasourceUid = "prometheus-production",
                    Expression = "latency{environment=\"{{environment}}\"}",
                    WarningAbove = 1.5
                }
            ]
        },
        VictoriaLogs = new VictoriaLogsScope
        {
            AccountId = "1",
            ProjectId = "20",
            StreamFilters = new Dictionary<string, string> { ["stream"] = "payments-production-stream" },
            Queries = [new VictoriaLogsQuery { Name = "Errors", Expression = "level:error" }]
        }
    };

    private sealed class PlannerHandler(string envelope) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(envelope, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class HttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}

file static class SlackKafkaProfileExtensions
{
    public static InvestigationProfile WithKafka(
        this InvestigationProfile profile,
        KafkaProfileScope kafka) => new()
        {
            Id = profile.Id,
            PagerDutyServiceId = profile.PagerDutyServiceId,
            Team = profile.Team,
            SlackChannel = profile.SlackChannel,
            SlackPromptLabels = profile.SlackPromptLabels,
            Selectors = profile.Selectors,
            PagerDuty = profile.PagerDuty,
            Nomad = profile.Nomad,
            GitLab = profile.GitLab,
            Grafana = profile.Grafana,
            Kafka = kafka,
            VictoriaLogs = profile.VictoriaLogs
        };
}

public sealed class SlackPromptWorkflowTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-13T10:00:00Z");

    [Fact]
    public async Task MentionRunsPlannerSelectedDatasourceExistingSynthesisAndOneThreadedReply()
    {
        var harness = CreateHarness(SlackQueryPlanningTests.ValidPlan());
        var mention = Mention();

        await harness.Handler.HandleAsync(mention, CancellationToken.None);

        Assert.Equal(1, harness.Planner.Calls);
        Assert.Equal(1, harness.Grafana.Calls);
        Assert.Equal(0, harness.VictoriaLogs.Calls);
        Assert.Equal(1, harness.Synthesizer.Calls);
        var reply = Assert.Single(harness.Replies.Items);
        Assert.Equal(new SlackReplyTarget(mention.ChannelId, mention.MessageTimestamp), reply.Target);
        Assert.Contains("Evidence-grounded answer", reply.Text, StringComparison.Ordinal);
        Assert.Contains("Query plan (YAML)", reply.Text, StringComparison.Ordinal);
        Assert.Contains("HTTP 5xx rate", reply.Text, StringComparison.Ordinal);
        Assert.Equal(["HTTP 5xx rate"], harness.Grafana.QueryNames);
    }

    [Fact]
    public async Task InvalidPlannerSelectionMakesNoDatasourceOrSynthesisCalls()
    {
        var harness = CreateHarness(SlackQueryPlanningTests.ValidPlan(queryNames: ["invented query"]));

        await harness.Handler.HandleAsync(Mention(), CancellationToken.None);

        Assert.Equal(1, harness.Planner.Calls);
        Assert.Equal(0, harness.Grafana.Calls);
        Assert.Equal(0, harness.VictoriaLogs.Calls);
        Assert.Equal(0, harness.Synthesizer.Calls);
        var reply = Assert.Single(harness.Replies.Items);
        Assert.Equal(SlackMentionHandler.FailureReply, reply.Text);
    }

    [Fact]
    public async Task UnmappedChannelMakesNoPlannerOrDatasourceCalls()
    {
        var harness = CreateHarness(SlackQueryPlanningTests.ValidPlan());

        await harness.Handler.HandleAsync(Mention(channelId: "C999"), CancellationToken.None);

        Assert.Equal(0, harness.Planner.Calls);
        Assert.Equal(0, harness.Grafana.Calls);
        Assert.Equal(0, harness.VictoriaLogs.Calls);
        Assert.Equal(0, harness.Synthesizer.Calls);
        var reply = Assert.Single(harness.Replies.Items);
        Assert.Equal(SlackMentionHandler.UnauthorizedReply, reply.Text);
    }

    [Fact]
    public async Task McpTransportMakesNoDatasourceOrSynthesisCalls()
    {
        var harness = CreateHarness(SlackQueryPlanningTests.ValidPlan(), grafanaMcp: true);

        await harness.Handler.HandleAsync(Mention(), CancellationToken.None);

        Assert.Equal(1, harness.Planner.Calls);
        Assert.Equal(0, harness.Grafana.Calls);
        Assert.Equal(0, harness.Synthesizer.Calls);
        var reply = Assert.Single(harness.Replies.Items);
        Assert.Equal(SlackMentionHandler.FailureReply, reply.Text);
    }

    private static Harness CreateHarness(SlackQueryPlan plan, bool grafanaMcp = false)
    {
        var profile = SlackQueryPlanningTests.Profile();
        var planner = new RecordingPlanner(plan);
        var grafana = new RecordingConnector("grafana");
        var victoriaLogs = new RecordingConnector("victorialogs");
        var sourceConfiguration = TestConfiguration.EvidenceSources(
            grafana: grafanaMcp
                ? new ConnectorTransport
                {
                    Mode = "mcp",
                    BaseUrl = "https://grafana.test",
                    Mcp = new McpToolConfiguration
                    {
                        ServerUrl = "https://mcp.test/grafana",
                        ToolName = "collect_evidence",
                        CredentialEnv = "MCP_TOKEN"
                    }
                }
                : null);
        var registry = new EvidenceSourceRegistry(
            [grafana, victoriaLogs],
            sourceConfiguration);
        var time = new FixedTimeProvider(Now);
        var collector = new AdaptiveEvidenceCollector(
            Microsoft.Extensions.Options.Options.Create(new IncidentBotOptions
            {
                EvidenceWindowMinutes = 30,
                EvidenceMaximumWindowMinutes = 30,
                EvidenceMaximumItems = 50,
                EvidenceMaximumBytes = 65536
            }),
            time,
            NullLogger<AdaptiveEvidenceCollector>.Instance);
        var synthesizer = new RecordingSynthesizer();
        var investigator = new SlackPromptInvestigator(
            new ProfileProvider(profile),
            planner,
            new SlackQueryPlanCompiler(new SafeTemplateRenderer()),
            registry,
            sourceConfiguration,
            collector,
            synthesizer,
            time,
            NullLogger<SlackPromptInvestigator>.Instance);
        var replies = new RecordingReplies();
        var handler = new SlackMentionHandler(
            investigator,
            replies,
            Microsoft.Extensions.Options.Options.Create(new SlackOptions
            {
                PromptTimeoutSeconds = 5,
                PromptChannelProfiles = new Dictionary<string, string>
                {
                    ["C123"] = "payments-production"
                }
            }),
            NullLogger<SlackMentionHandler>.Instance);
        return new Harness(handler, planner, grafana, victoriaLogs, synthesizer, replies);
    }

    private static SlackMention Mention(string channelId = "C123") => new(
        "Ev123",
        "T123",
        channelId,
        "U123",
        "1710000001.000002",
        null,
        "Are errors rising?");

    private sealed record Harness(
        SlackMentionHandler Handler,
        RecordingPlanner Planner,
        RecordingConnector Grafana,
        RecordingConnector VictoriaLogs,
        RecordingSynthesizer Synthesizer,
        RecordingReplies Replies);

    private sealed class ProfileProvider(InvestigationProfile profile) : ISlackQueryProfileProvider
    {
        public SlackQueryProfile Resolve(string profileId)
        {
            Assert.Equal(profile.Id, profileId);
            return new SlackQueryProfile("test-revision", profile);
        }
    }

    private sealed class RecordingPlanner(SlackQueryPlan plan) : ISlackQueryPlanner
    {
        public int Calls { get; private set; }

        public Task<SlackQueryPlan> PlanAsync(
            string prompt,
            InvestigationProfile profile,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(plan);
        }
    }

    private sealed class RecordingConnector(string source) : IIncidentEvidenceConnector
    {
        public string Source => source;
        public int Calls { get; private set; }
        public IReadOnlyList<string> QueryNames { get; private set; } = [];

        public Task<ConnectorResult> CollectAsync(
            InvestigationContext context,
            EvidenceScope scope,
            CancellationToken cancellationToken)
        {
            Calls++;
            QueryNames = Source switch
            {
                "grafana" => context.Profile.Grafana?.Queries.Select(query => query.Name).ToArray() ?? [],
                "victorialogs" => context.Profile.VictoriaLogs?.Queries.Select(query => query.Name).ToArray() ?? [],
                _ => []
            };
            var finding = new EvidenceFinding(
                $"{Source}-finding",
                Source,
                Now,
                null,
                "metric",
                "warning",
                "HTTP 5xx rose above the reviewed threshold",
                null,
                null,
                .9,
                new JsonObject());
            return Task.FromResult(new ConnectorResult(
                Source,
                SourceHealth.Complete,
                [finding],
                [],
                [],
                1,
                null));
        }
    }

    private sealed class RecordingSynthesizer : IInvestigationSynthesizer
    {
        public int Calls { get; private set; }

        public Task<AiSynthesis> SynthesizeAsync(
            InvestigationSubject subject,
            IReadOnlyList<ConnectorResult> results,
            AiSynthesis? previous,
            CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Single(results);
            Assert.Equal("grafana", results[0].Source);
            return Task.FromResult(new AiSynthesis(
                "complete",
                "Evidence-grounded answer",
                [],
                [],
                [],
                "hash"));
        }
    }

    private sealed class RecordingReplies : ISlackReplyPublisher
    {
        public List<(SlackReplyTarget Target, string Text)> Items { get; } = [];

        public Task ReplyAsync(
            SlackReplyTarget target,
            string text,
            CancellationToken cancellationToken)
        {
            Items.Add((target, text));
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
