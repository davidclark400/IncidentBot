using Panko.Api.Cases;
using Microsoft.Extensions.Logging.Abstractions;

namespace Panko.Api.Tests;

public sealed class DurableWorkerTests
{
    [Fact]
    public async Task SuccessfulItemIsCompleted()
    {
        var queue = new RecordingQueue("item");
        var worker = new TestWorker(queue, fail: false);

        Assert.True(await worker.ProcessNextAsync(CancellationToken.None));

        Assert.Equal("item", Assert.Single(queue.Completed));
        Assert.Empty(queue.Failed);
    }

    [Fact]
    public async Task FailedItemIsReleasedForRetry()
    {
        var queue = new RecordingQueue("item");
        var worker = new TestWorker(queue, fail: true);

        Assert.True(await worker.ProcessNextAsync(CancellationToken.None));

        Assert.Empty(queue.Completed);
        Assert.Equal("item", Assert.Single(queue.Failed));
    }

    private sealed class TestWorker(IDurableQueue<string> queue, bool fail) :
        DurableWorker<string>(queue, TimeSpan.Zero, NullLogger.Instance)
    {
        protected override Task ProcessAsync(string item, CancellationToken cancellationToken) =>
            fail ? Task.FromException(new InvalidOperationException("failed")) : Task.CompletedTask;
    }

    private sealed class RecordingQueue(string? item) : IDurableQueue<string>
    {
        public List<string> Completed { get; } = [];
        public List<string> Failed { get; } = [];

        public Task<string?> LeaseAsync(CancellationToken cancellationToken)
        {
            var leased = item;
            item = null;
            return Task.FromResult(leased);
        }

        public Task CompleteAsync(string completed, CancellationToken cancellationToken)
        {
            Completed.Add(completed);
            return Task.CompletedTask;
        }

        public Task FailAsync(string failed, Exception exception, CancellationToken cancellationToken)
        {
            Failed.Add(failed);
            return Task.CompletedTask;
        }
    }
}
