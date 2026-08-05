using System.Text.Json;
using Panko.Api.Cases;
using Panko.Api.Options;
using Panko.Contracts;
using Microsoft.Extensions.Options;

namespace Panko.Api.Tests;

public sealed class CaseInputBoundaryTests
{
    private static readonly Guid CaseId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset ReferenceTime =
        DateTimeOffset.Parse("2026-08-03T09:45:00Z");

    [Fact]
    public void SubmittedCrumbKindUsesTheCanonicalWireValue()
    {
        Assert.Equal("\"crumb\"", JsonSerializer.Serialize(SubmittedCrumbKind.Crumb));
        Assert.Equal(
            SubmittedCrumbKind.Crumb,
            JsonSerializer.Deserialize<SubmittedCrumbKind>("\"crumb\""));
    }

    [Fact]
    public void NormalizeBoundsAndCanonicalizesCallerControlledFields()
    {
        var boundary = Boundary(new CaseOptions
        {
            MaximumSummaryCharacters = 32,
            MaximumExcerptCharacters = 8,
            MaximumAttributesBytes = 1024
        });
        var attributes = new Dictionary<string, JsonElement>
        {
            ["z-last"] = Json("2"),
            ["a-first"] = Json("1")
        };
        var submitted = SubmittedInput() with
        {
            ClientCrumbId = "  event-001  ",
            Category = "  DEPLOYMENT  ",
            Severity = "  WARNING  ",
            Summary = $"  {new string('s', 50)}  ",
            Excerpt = "  excerpt-is-long  ",
            DeclaredSource = "  GitLab  ",
            Actor = $"  {new string('a', 220)}  ",
            Url = "https://gitlab.example/jobs/42?view=trace#fragment",
            Attributes = attributes
        };

        var normalized = boundary.Normalize(
            CaseId,
            "agent@example.internal",
            ReferenceTime,
            ["deployment"],
            [submitted]).Single();

        Assert.Equal("event-001", normalized.ClientCrumbId);
        Assert.Equal("deployment", normalized.Category);
        Assert.Equal("warning", normalized.Severity);
        Assert.Equal(new string('s', 32), normalized.Summary);
        Assert.Equal("excerpt-", normalized.Excerpt);
        Assert.Equal("gitlab", normalized.DeclaredSource);
        Assert.Equal(200, normalized.Actor?.Length);
        Assert.Equal("https://gitlab.example/jobs/42?view=trace", normalized.Url);
        Assert.Equal(new[] { "a-first", "z-last" }, normalized.Attributes.Select(item => item.Key));
        Assert.Equal(
            CaseInputBoundary.DeterministicCrumbId(
                CaseId,
                "agent@example.internal",
                "event-001"),
            normalized.Id);
        Assert.Matches("^[0-9a-f]{64}$", normalized.PayloadHash);
    }

    [Fact]
    public void DeterministicInputIdsAreStableAndScopedToCaseProducerAndClientCrumbId()
    {
        var first = CaseInputBoundary.DeterministicCrumbId(
            CaseId,
            "agent-a",
            "event-001");

        Assert.Equal(
            first,
            CaseInputBoundary.DeterministicCrumbId(
                CaseId,
                "agent-a",
                "event-001"));
        Assert.NotEqual(
            first,
            CaseInputBoundary.DeterministicCrumbId(
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                "agent-a",
                "event-001"));
        Assert.NotEqual(
            first,
            CaseInputBoundary.DeterministicCrumbId(
                CaseId,
                "agent-b",
                "event-001"));
        Assert.NotEqual(
            first,
            CaseInputBoundary.DeterministicCrumbId(
                CaseId,
                "agent-a",
                "event-002"));
        Assert.Equal('5', first.ToString("D")[14]);
    }

    [Fact]
    public void PayloadHashIsIndependentOfTopLevelAttributeInsertionOrder()
    {
        var boundary = Boundary();
        var forward = SubmittedInput() with
        {
            Attributes = new Dictionary<string, JsonElement>
            {
                ["component"] = Json("\"payments\""),
                ["replicas"] = Json("3")
            }
        };
        var reverse = forward with
        {
            Attributes = new Dictionary<string, JsonElement>
            {
                ["replicas"] = Json("3"),
                ["component"] = Json("\"payments\"")
            }
        };

        var first = boundary.Normalize(
            CaseId, "agent-a", ReferenceTime, ["deployment"], [forward]).Single();
        var second = boundary.Normalize(
            CaseId, "agent-a", ReferenceTime, ["deployment"], [reverse]).Single();

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.PayloadHash, second.PayloadHash);
    }

    [Fact]
    public void NormalizeRejectsInvalidCategorySeverityAndTimestamp()
    {
        var boundary = Boundary(new CaseOptions
        {
            MaximumTimestampDistanceHours = 2
        });

        var category = Assert.Throws<CaseValidationException>(() => boundary.Normalize(
            CaseId, "agent", ReferenceTime, ["deployment"],
            [SubmittedInput() with { Category = "arbitrary-query" }]));
        var severity = Assert.Throws<CaseValidationException>(() => boundary.Normalize(
            CaseId, "agent", ReferenceTime, ["deployment"],
            [SubmittedInput() with { Severity = "emergency" }]));
        var timestamp = Assert.Throws<CaseValidationException>(() => boundary.Normalize(
            CaseId, "agent", ReferenceTime, ["deployment"],
            [SubmittedInput() with { OccurredAt = ReferenceTime.AddHours(3) }]));
        var type = Assert.Throws<CaseValidationException>(() => boundary.Normalize(
            CaseId, "agent", ReferenceTime, ["deployment"],
            [SubmittedInput() with { Kind = (SubmittedCrumbKind)999 }]));

        Assert.Contains("not allowed", category.Message, StringComparison.Ordinal);
        Assert.Contains("Severity", severity.Message, StringComparison.Ordinal);
        Assert.Contains("occurredAt", timestamp.Message, StringComparison.Ordinal);
        Assert.Contains("Input type", type.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeRejectsEmptyOversizedAndDuplicateBatches()
    {
        var boundary = Boundary(new CaseOptions
        {
            MaximumInputsPerBatch = 1,
            MaximumRequestBytes = 1024
        });

        Assert.Throws<CaseValidationException>(() => boundary.Normalize(
            CaseId, "agent", ReferenceTime, ["deployment"], []));
        Assert.Throws<CaseValidationException>(() => boundary.Normalize(
            CaseId, "agent", ReferenceTime, ["deployment"],
            [SubmittedInput(), SubmittedInput() with { ClientCrumbId = "event-002" }]));

        var duplicateBoundary = Boundary();
        var prefix = new string('x', 128);
        var duplicate = Assert.Throws<CaseValidationException>(() => duplicateBoundary.Normalize(
            CaseId, "agent", ReferenceTime, ["deployment"],
            [
                SubmittedInput() with { ClientCrumbId = prefix + "-one" },
                SubmittedInput() with { ClientCrumbId = prefix + "-two" }
            ]));

        Assert.Contains("occurs more than once", duplicate.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeRejectsOversizedRequestsAttributesAndExcessiveDepth()
    {
        var requestBoundary = Boundary(new CaseOptions
        {
            MaximumRequestBytes = 1024,
            MaximumExcerptCharacters = 8_000
        });
        var request = Assert.Throws<CaseValidationException>(() => requestBoundary.Normalize(
            CaseId, "agent", ReferenceTime, ["deployment"],
            [SubmittedInput() with { Excerpt = new string('x', 2_000) }]));

        var attributesBoundary = Boundary(new CaseOptions
        {
            MaximumRequestBytes = 32_000,
            MaximumAttributesBytes = 128,
            MaximumAttributesDepth = 2
        });
        var bytes = Assert.Throws<CaseValidationException>(() => attributesBoundary.Normalize(
            CaseId, "agent", ReferenceTime, ["deployment"],
            [SubmittedInput() with
            {
                Attributes = new Dictionary<string, JsonElement>
                {
                    ["detail"] = Json($"\"{new string('x', 200)}\"")
                }
            }]));
        var depth = Assert.Throws<CaseValidationException>(() => attributesBoundary.Normalize(
            CaseId, "agent", ReferenceTime, ["deployment"],
            [SubmittedInput() with
            {
                Attributes = new Dictionary<string, JsonElement>
                {
                    ["detail"] = Json("{\"nested\":{\"tooDeep\":true}}")
                }
            }]));

        Assert.Contains("batch is too large", request.Message, StringComparison.Ordinal);
        Assert.Contains("byte limit", bytes.Message, StringComparison.Ordinal);
        Assert.Contains("depth limit", depth.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeRejectsSensitiveKeysAndCredentialLikeContentRecursively()
    {
        var boundary = Boundary();
        var sensitiveKey = Assert.Throws<CaseValidationException>(() => boundary.Normalize(
            CaseId, "agent", ReferenceTime, ["deployment"],
            [SubmittedInput() with
            {
                Attributes = new Dictionary<string, JsonElement>
                {
                    ["context"] = Json("{\"nested\":{\"api-token\":\"unsafe\"}}")
                }
            }]));
        var credential = Assert.Throws<CaseValidationException>(() => boundary.Normalize(
            CaseId, "agent", ReferenceTime, ["deployment"],
            [SubmittedInput() with { Summary = "Observed Authorization: Bearer abcdefghijklmnop" }]));
        var sensitiveUrl = Assert.Throws<CaseValidationException>(() => boundary.Normalize(
            CaseId, "agent", ReferenceTime, ["deployment"],
            [SubmittedInput() with { Url = "https://example.internal/event?access_token=unsafe" }]));
        var credentialInUrlValue = Assert.Throws<CaseValidationException>(() => boundary.Normalize(
            CaseId, "agent", ReferenceTime, ["deployment"],
            [SubmittedInput() with
            {
                Url = "https://example.internal/event?detail=Bearer%20abcdefghijklmnop"
            }]));
        var formEncodedCredentialInUrlValue = Assert.Throws<CaseValidationException>(() => boundary.Normalize(
            CaseId, "agent", ReferenceTime, ["deployment"],
            [SubmittedInput() with
            {
                Url = "https://example.internal/event?detail=Bearer+abcdefghijklmnop"
            }]));

        Assert.Contains("Sensitive attribute key", sensitiveKey.Message, StringComparison.Ordinal);
        Assert.Contains("Credential-like content", credential.Message, StringComparison.Ordinal);
        Assert.Contains("Sensitive URL query", sensitiveUrl.Message, StringComparison.Ordinal);
        Assert.Contains("Credential-like content", credentialInUrlValue.Message, StringComparison.Ordinal);
        Assert.Contains("Credential-like content", formEncodedCredentialInUrlValue.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ftp://example.internal/event")]
    [InlineData("https://user:password@example.internal/event")]
    [InlineData("not-an-absolute-url")]
    public void NormalizeRejectsUnsafeUrls(string url)
    {
        var boundary = Boundary();

        var exception = Assert.Throws<CaseValidationException>(() => boundary.Normalize(
            CaseId, "agent", ReferenceTime, ["deployment"],
            [SubmittedInput() with { Url = url }]));

        Assert.Contains("HTTP or HTTPS", exception.Message, StringComparison.Ordinal);
    }

    private static CaseInputBoundary Boundary(
        CaseOptions? options = null) =>
        new(Microsoft.Extensions.Options.Options.Create(options ?? new CaseOptions()));

    private static SubmittedCrumb SubmittedInput() => new(
        "event-001",
        SubmittedCrumbKind.Event,
        ReferenceTime,
        "deployment",
        "warning",
        "Deployment completed");

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
