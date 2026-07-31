using IncidentBot.Api.Connectors;
using IncidentBot.Api.Domain;
using IncidentBot.Api.Incidents;
using IncidentBot.Api.Options;
using IncidentBot.Api.Profiles;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace IncidentBot.Api.Tests;

public sealed class IncidentIntakeTests
{
    private const string ProfileDocument = """
        version: 2
        revision: test-v2
        fallbackSlackChannel: "#incidents"
        profiles:
          - id: payments-blue
            pagerDutyServiceId: P123PAYMENTS
            team: payments
            slackChannel: "#payments-incidents"
            selectors:
              - labels:
                  tenant: blue
        """;

    [Fact]
    public async Task AcceptsWithSelectedProfileApprovedLabelsAndUnchangedRawPayload()
    {
        using var fixture = new ProfileFixture(ProfileDocument);
        var incidents = new RecordingIncidentStore(isDuplicate: true);
        IIncidentIntake intake = new IncidentIntake(fixture.Store, incidents);
        var rawPayload = new byte[] { 0, 1, 2, 3, 255 };

        var result = await intake.AcceptAsync(
            Webhook(new Dictionary<string, string>
            {
                ["service"] = "P123PAYMENTS",
                ["environment"] = "production",
                ["tenant"] = "blue",
                ["diagnostic_noise"] = "not needed after routing",
                ["auth_token"] = "must-not-persist"
            }),
            rawPayload,
            CancellationToken.None);

        Assert.Equal(incidents.IncidentId, result.IncidentId);
        Assert.True(result.IsDuplicate);
        Assert.Equal("payments-blue", incidents.AcceptedProfile!.Id);
        Assert.Equal("production", incidents.AcceptedWebhook!.Labels["environment"]);
        Assert.Equal("blue", incidents.AcceptedWebhook.Labels["tenant"]);
        Assert.DoesNotContain("diagnostic_noise", incidents.AcceptedWebhook.Labels.Keys);
        Assert.DoesNotContain("auth_token", incidents.AcceptedWebhook.Labels.Keys);
        Assert.Equal(rawPayload, incidents.RawPayload);
    }

    [Fact]
    public async Task ProfileSelectionFailureIsExplicitAndDoesNotReachPersistence()
    {
        using var fixture = new ProfileFixture(ProfileDocument);
        var incidents = new RecordingIncidentStore();
        IIncidentIntake intake = new IncidentIntake(fixture.Store, incidents);

        var exception = await Assert.ThrowsAsync<InvestigationProfileSelectionException>(() =>
            intake.AcceptAsync(
                Webhook(new Dictionary<string, string>
                {
                    ["service"] = "P123PAYMENTS",
                    ["tenant"] = "red"
                }),
                new byte[] { 1 },
                CancellationToken.None));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("No investigation profile selector matched", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, incidents.AcceptCalls);
    }

    [Fact]
    public async Task PersistenceInvalidOperationIsNotMisclassifiedAsProfileSelectionFailure()
    {
        using var fixture = new ProfileFixture(ProfileDocument);
        var repositoryFailure = new InvalidOperationException("incident upsert failed");
        var incidents = new RecordingIncidentStore(repositoryFailure);
        IIncidentIntake intake = new IncidentIntake(fixture.Store, incidents);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            intake.AcceptAsync(
                Webhook(new Dictionary<string, string>
                {
                    ["service"] = "P123PAYMENTS",
                    ["tenant"] = "blue"
                }),
                new byte[] { 1 },
                CancellationToken.None));

        Assert.Same(repositoryFailure, exception);
        Assert.IsNotType<InvestigationProfileSelectionException>(exception);
        Assert.Equal(1, incidents.AcceptCalls);
    }

    private static PagerDutyWebhookEvent Webhook(IReadOnlyDictionary<string, string> labels) => new(
        "event-1",
        "incident.triggered",
        "PINCIDENT",
        "P123PAYMENTS",
        "Checkout latency",
        "high",
        "https://pagerduty.example/incidents/PINCIDENT",
        DateTimeOffset.Parse("2026-07-14T08:00:00Z"),
        DateTimeOffset.Parse("2026-07-14T08:05:00Z"),
        labels);

    private sealed class ProfileFixture : IDisposable
    {
        private readonly string path = Path.Combine(
            Path.GetTempPath(),
            $"incidentbot-intake-profile-{Guid.NewGuid():N}.yaml");

        public ProfileFixture(string document)
        {
            File.WriteAllText(path, document);
            Store = new InvestigationProfileStore(
                Microsoft.Extensions.Options.Options.Create(new IncidentBotOptions { ProfilesPath = path }),
                new TestEnvironment(),
                new EvidenceSourceRegistry(
                    Array.Empty<IIncidentEvidenceConnector>(),
                    TestConfiguration.EvidenceSources()));
        }

        public InvestigationProfileStore Store { get; }

        public void Dispose() => File.Delete(path);
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "IncidentBot.Api.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class RecordingIncidentStore(
        Exception? acceptFailure = null,
        bool isDuplicate = false) : IIncidentStore
    {
        public Guid IncidentId { get; } = Guid.NewGuid();
        public int AcceptCalls { get; private set; }
        public PagerDutyWebhookEvent? AcceptedWebhook { get; private set; }
        public InvestigationProfile? AcceptedProfile { get; private set; }
        public byte[]? RawPayload { get; private set; }

        public Task<(Guid IncidentId, bool IsDuplicate)> AcceptWebhookAsync(
            PagerDutyWebhookEvent webhook,
            InvestigationProfile profile,
            ReadOnlyMemory<byte> rawPayload,
            CancellationToken cancellationToken)
        {
            AcceptCalls++;
            AcceptedWebhook = webhook;
            AcceptedProfile = profile;
            RawPayload = rawPayload.ToArray();
            return acceptFailure is null
                ? Task.FromResult((IncidentId, isDuplicate))
                : Task.FromException<(Guid IncidentId, bool IsDuplicate)>(acceptFailure);
        }

        public Task<IncidentRecord?> GetIncidentAsync(Guid incidentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<InvestigationReport?> GetReportAsync(Guid incidentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> SaveReportAsync(
            IncidentRecord incident,
            InvestigationReport report,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetStatusAsync(Guid incidentId, string status, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> RestartInvestigationAsync(
            Guid incidentId,
            string? slackChannel,
            string? slackTimestamp,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetSlackTimestampAsync(
            Guid incidentId,
            string timestamp,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
