using System.Text;
using IncidentBot.Api.Connectors;

namespace IncidentBot.Api.Tests;

public sealed class ConnectorUtilitiesTests
{
    [Fact]
    public async Task BoundedJsonAccountsForTheOverflowingByte()
    {
        var observed = 0;
        await using var stream = new NonSeekableMemoryStream(Encoding.UTF8.GetBytes("[123456789]"));
        using var response = new HttpResponseMessage
        {
            Content = new StreamContent(stream)
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ConnectorUtilities.ReadBoundedJsonAsync(
                response, maxBytes: 5, CancellationToken.None, count => observed += count));

        Assert.Contains("byte limit", exception.Message, StringComparison.Ordinal);
        Assert.Equal(6, observed);
        Assert.Equal(6, stream.BytesRead);
    }

    [Fact]
    public async Task DeclaredOversizeExhaustsTheSharedAllowanceWithoutReadingTheBody()
    {
        var observed = 0;
        await using var stream = new NonSeekableMemoryStream(new byte[100]);
        using var response = new HttpResponseMessage
        {
            Content = new StreamContent(stream)
        };
        response.Content.Headers.ContentLength = 100;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ConnectorUtilities.ReadBoundedJsonAsync(
                response, maxBytes: 10, CancellationToken.None, count => observed += count));

        Assert.Equal(10, observed);
        Assert.Equal(0, stream.BytesRead);
    }

    private sealed class NonSeekableMemoryStream(byte[] buffer) : MemoryStream(buffer, writable: false)
    {
        public int BytesRead { get; private set; }
        public override bool CanSeek => false;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken = default)
        {
            var count = await base.ReadAsync(destination, cancellationToken);
            BytesRead += count;
            return count;
        }
    }
}
