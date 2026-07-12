using IncidentBot.Api.Connectors;
using IncidentBot.Api.Domain;

namespace IncidentBot.Api.Tests;

public sealed class ConnectorTransportTests
{
    [Fact]
    public async Task McpModeUsesTheMcpAdapter()
    {
        var mcp = new RecordingMcpAdapter();
        var nativeCalled = false;

        var result = await ConnectorUtilities.CollectAsync(
            "source",
            new ConnectorTransport
            {
                Mode = "mcp",
                Mcp = new McpToolConfiguration(),
                TimeoutSeconds = 5,
                MaxItems = 3,
                MaxBytes = 512
            },
            mcp,
            Context(),
            Scope(),
            new { resource = "allowed" },
            _ =>
            {
                nativeCalled = true;
                return Task.FromResult(Result("native"));
            },
            CancellationToken.None);

        Assert.True(mcp.WasCalled);
        Assert.False(nativeCalled);
        Assert.Equal("mcp", result.Source);
        Assert.Equal(3, mcp.ReceivedScope?.MaxItems);
        Assert.Equal(512, mcp.ReceivedScope?.MaxBytes);
    }

    [Fact]
    public async Task NativeModeDoesNotCrossTheMcpSeam()
    {
        var mcp = new RecordingMcpAdapter();

        var result = await ConnectorUtilities.CollectAsync(
            "source",
            new ConnectorTransport { Mode = "api", TimeoutSeconds = 5 },
            mcp,
            Context(),
            Scope(),
            new { resource = "allowed" },
            _ => Task.FromResult(Result("native")),
            CancellationToken.None);

        Assert.False(mcp.WasCalled);
        Assert.Equal("native", result.Source);
    }

    private static InvestigationContext Context() => new(
        Guid.NewGuid(), "PD-1", "payments", "Incident", "high", IncidentState.Triggered,
        DateTimeOffset.Parse("2026-07-11T10:00:00Z"), new Dictionary<string, string>(),
        new InvestigationProfile { Id = "profile" });

    private static EvidenceScope Scope() => new(
        DateTimeOffset.Parse("2026-07-11T09:30:00Z"),
        DateTimeOffset.Parse("2026-07-11T10:00:00Z"), "v1", 10, 1024);

    private static ConnectorResult Result(string source) =>
        new(source, SourceHealth.Complete, [], [], [], 0, null);

    private sealed class RecordingMcpAdapter : IMcpEvidenceAdapter
    {
        public bool WasCalled { get; private set; }
        public EvidenceScope? ReceivedScope { get; private set; }

        public Task<ConnectorResult> CollectAsync(
            string source,
            McpToolConfiguration configuration,
            InvestigationContext context,
            EvidenceScope scope,
            object allowedResources,
            string? allowedBaseUrl,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            ReceivedScope = scope;
            return Task.FromResult(Result("mcp"));
        }
    }
}
