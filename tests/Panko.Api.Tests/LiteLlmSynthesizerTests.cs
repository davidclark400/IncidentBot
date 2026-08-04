using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Panko.Api.Domain;
using Panko.Api.Cases;
using Panko.Api.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace Panko.Api.Tests;

public sealed class LiteLlmSynthesizerTests
{
    [Fact]
    public async Task SemanticallySuppressedCrumbIdsCannotBecomeDiagnosisCitations()
    {
        var caseSubject = Subject();
        var crumbs = Enumerable.Range(1, 8)
            .Select(index => new Crumb(
                $"log-{index:D2}",
                "victorialogs",
                caseSubject.OpenedAt.AddSeconds(index),
                null,
                "log-sample",
                "warning",
                $"Checkout timeout for request 550e8400-e29b-41d4-a716-{index:D12}.",
                null,
                null,
                .9,
                new JsonObject
                {
                    ["scope"] = new JsonObject { ["Name"] = "checkout-timeouts" }
                },
                ObjectType: "log-query",
                ObjectId: "checkout-timeouts"))
            .ToList();
        var results = new[]
        {
            new CrumbSourceResult("victorialogs", CrumbSourceHealth.Complete, crumbs, [], [], 10, null)
        };
        const int budget = 1_200;
        var payload = LiteLlmSynthesizer.BuildDigestPayload(caseSubject, results, budget);
        Assert.True(payload.SemanticCompressionApplied);
        var emittedId = Assert.Single(payload.CrumbIds.Take(1));
        var suppressedId = Assert.Single(crumbs
            .Where(crumb => !payload.CrumbIds.Contains(crumb.Id))
            .Select(crumb => crumb.Id)
            .Take(1));
        var responseContent = JsonSerializer.Serialize(new
        {
            summaryParts = new[] { new { text = "Compressed synthesis.", referenceId = (string?)null } },
            possibleContributors = Array.Empty<string>(),
            unknowns = Array.Empty<string>(),
            recommendedChecks = Array.Empty<string>(),
            diagnoses = new[]
            {
                new
                {
                    summary = "Repeated checkout timeout",
                    crumbIds = new[] { emittedId, suppressedId },
                    codeReferenceIds = Array.Empty<string>(),
                    rank = 1,
                    crumbStrength = 90
                }
            }
        });
        var envelope = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = responseContent } } }
        });
        var synthesizer = CreateSynthesizer(new StaticHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/json")
        }), budget);

        var synthesis = await synthesizer.SynthesizeAsync(caseSubject, results, null, CancellationToken.None);

        var diagnosis = Assert.Single(synthesis.Diagnoses!);
        Assert.Equal([emittedId], diagnosis.CrumbIds);
        Assert.DoesNotContain(suppressedId, diagnosis.CrumbIds);
    }

    [Fact]
    public async Task SynthesisAcceptsOnlyIdentifiersActuallySerializedIntoDigest()
    {
        const int budget = 24_000;
        var caseSubject = Subject();
        var results = Enumerable.Range(1, 60)
            .Select(index => Result(index))
            .ToList();
        var payload = LiteLlmSynthesizer.BuildDigestPayload(caseSubject, results, budget);
        var allCrumbs = results.SelectMany(result => result.Crumbs).ToList();
        var allReferences = LiteLlmSynthesizer.BuildReferenceCatalog(results, caseSubject.OpenedAt);

        var emittedCrumbId = Assert.Single(payload.CrumbIds.Take(1));
        var omittedCrumbId = Assert.Single(allCrumbs
            .Where(crumb => !payload.CrumbIds.Contains(crumb.Id))
            .Select(crumb => crumb.Id)
            .Take(1));
        var emittedCodeReferenceId = Assert.Single(payload.CodeReferenceIds.Take(1));
        var omittedCodeReferenceId = Assert.Single(allCrumbs
            .SelectMany(crumb => crumb.CodeReferences ?? [])
            .Where(reference => !payload.CodeReferenceIds.Contains(reference.Id))
            .Select(reference => reference.Id)
            .Take(1));
        var emittedReferenceId = Assert.Single(payload.ReferenceIds.Take(1));
        var omittedReferenceId = Assert.Single(allReferences
            .Where(reference => !payload.ReferenceIds.Contains(reference.Id))
            .Select(reference => reference.Id)
            .Take(1));

        var responseContent = JsonSerializer.Serialize(new
        {
            summaryParts = new object[]
            {
                new { text = "Supported summary. ", referenceId = emittedReferenceId },
                new { text = "Unsupported link is removed.", referenceId = omittedReferenceId }
            },
            possibleContributors = Array.Empty<string>(),
            unknowns = Array.Empty<string>(),
            recommendedChecks = Array.Empty<string>(),
            diagnoses = new[]
            {
                new
                {
                    summary = "Bounded diagnosis",
                    crumbIds = new[] { emittedCrumbId, omittedCrumbId },
                    codeReferenceIds = new[] { emittedCodeReferenceId, omittedCodeReferenceId },
                    rank = 1,
                    crumbStrength = 90
                }
            }
        });
        var envelope = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = responseContent } } }
        });
        var synthesizer = CreateSynthesizer(new StaticHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/json")
        }), budget);

        var synthesis = await synthesizer.SynthesizeAsync(caseSubject, results, null, CancellationToken.None);

        Assert.Equal("complete", synthesis.Status);
        var diagnosis = Assert.Single(synthesis.Diagnoses!);
        Assert.Equal([emittedCrumbId], diagnosis.CrumbIds);
        Assert.Equal([emittedCodeReferenceId], diagnosis.CodeReferences.Select(reference => reference.Id));
        Assert.DoesNotContain(omittedCrumbId, diagnosis.CrumbIds);
        Assert.DoesNotContain(omittedCodeReferenceId, diagnosis.CodeReferences.Select(reference => reference.Id));
        Assert.Contains(synthesis.SummaryParts!, part => part.ReferenceId == emittedReferenceId);
        Assert.Contains(synthesis.SummaryParts!, part => part.Text.Contains("Unsupported", StringComparison.Ordinal)
            && part.ReferenceId is null);
        Assert.DoesNotContain(synthesis.SummaryReferences!, reference => reference.Id == omittedReferenceId);
    }

    [Fact]
    public async Task SynthesisRejectsResponseEnvelopeAboveStrictByteLimit()
    {
        var oversized = new ByteArrayContent(new byte[LiteLlmSynthesizer.MaximumResponseBytes + 1]);
        oversized.Headers.ContentType = new("application/json");
        var synthesizer = CreateSynthesizer(new StaticHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = oversized
        }), 12_000);

        var synthesis = await synthesizer.SynthesizeAsync(
            Subject(),
            [Result(1)],
            null,
            CancellationToken.None);

        Assert.Equal("unavailable", synthesis.Status);
    }

    [Fact]
    public async Task BoundedResponseReaderRejectsStreamingBodyWithoutContentLength()
    {
        using var content = new UnknownLengthContent(new byte[17]);
        Assert.Null(content.Headers.ContentLength);

        var exception = await Assert.ThrowsAsync<JsonException>(() =>
            LiteLlmSynthesizer.ReadBoundedResponseAsync(content, 16, CancellationToken.None));

        Assert.Contains("16-byte limit", exception.Message, StringComparison.Ordinal);
    }

    private static LiteLlmSynthesizer CreateSynthesizer(HttpMessageHandler handler, int budget) => new(
        new StubHttpClientFactory(handler),
        Microsoft.Extensions.Options.Options.Create(new LiteLlmOptions
        {
            BaseUrl = "http://litellm.test",
            Model = "test-model",
            ApiKeyEnv = "UNSET_TEST_LITELLM_API_KEY",
            TimeoutSeconds = 5,
            InputCharacterBudget = budget,
            MaxOutputTokens = 1_000
        }),
        TestConfiguration.Credentials(),
        NullLogger<LiteLlmSynthesizer>.Instance);

    private static CaseSubject Subject() => new(
        "payments",
        "Case title",
        "high",
        PagerDutyIncidentState.Triggered,
        DateTimeOffset.Parse("2026-07-11T10:00:00Z"));

    private static CrumbSourceResult Result(int index)
    {
        var id = $"crumb-{index:D3}";
        var reference = new CodeReference(
            $"code-{index:D3}",
            $"platform/project-{index:D3}",
            "abcdef123456",
            "src/Handler.cs",
            40,
            42,
            $"https://gitlab.test/project-{index:D3}/blob/abcdef/src/Handler.cs#L40-42",
            "+changed");
        var crumb = new Crumb(
            id,
            $"source-{index:D3}",
            DateTimeOffset.Parse("2026-07-11T10:00:00Z").AddSeconds(index),
            null,
            "code-diff",
            "warning",
            $"Changed handler {index:D3} {new string('x', 450)}",
            null,
            $"https://gitlab.test/project-{index:D3}/merge_requests/1",
            .9,
            new JsonObject(),
            "developer",
            "merge-request",
            index.ToString(),
            [reference]);
        return new CrumbSourceResult(crumb.Source, CrumbSourceHealth.Complete, [crumb], [], [], 10, null);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StaticHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }
}
