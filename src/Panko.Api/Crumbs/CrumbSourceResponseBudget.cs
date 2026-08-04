using System.Text.Json;

namespace Panko.Api.Crumbs;

/// <summary>
/// Runs a planned sequence of Crumb source response reads under one cumulative byte limit.
/// Each operation receives a fair share of the remaining bytes, while unused capacity
/// flows forward to later operations. A null read means the operation was skipped or
/// constrained by the byte limit; other transport and parsing failures remain visible.
/// </summary>
internal sealed class CrumbSourceResponseBudget
{
    private readonly int maximumBytes;
    private readonly List<string> limitedOperations = [];
    private int consumedBytes;
    private int remainingOperations;

    public CrumbSourceResponseBudget(
        int scopeMaximumBytes,
        int sourceMaximumBytes,
        int plannedOperations)
    {
        maximumBytes = Math.Min(
            Math.Max(0, scopeMaximumBytes),
            Math.Max(0, sourceMaximumBytes));
        remainingOperations = Math.Max(0, plannedOperations);
    }

    public bool IsPartial => limitedOperations.Count > 0;

    public string? Diagnostic => IsPartial
        ? CrumbSourceUtilities.CombineDiagnostics(
            $"Cumulative source response byte budget constrained collection: used {consumedBytes} of "
            + $"{maximumBytes} bytes; limited {limitedOperations.Count} request(s): "
            + string.Join(", ", limitedOperations.Take(6))
            + (limitedOperations.Count > 6 ? ", …" : ""))
        : null;

    /// <summary>
    /// Removes a conditional operation that is no longer needed. Its unused share is
    /// redistributed across the remaining planned operations.
    /// </summary>
    public void SkipPlannedOperation()
    {
        if (remainingOperations > 0) remainingOperations--;
    }

    public Task<JsonDocument?> TryReadJsonAsync(
        string operation,
        Func<CancellationToken, Task<HttpResponseMessage>> sendAsync,
        CancellationToken cancellationToken) =>
        TryReadAsync(
            operation,
            sendAsync,
            (response, maximumReadBytes, observeBytesRead, ct) =>
                CrumbSourceUtilities.ReadBoundedJsonAsync(
                    response,
                    maximumReadBytes,
                    ct,
                    observeBytesRead),
            cancellationToken);

    public Task<string?> TryReadTextAsync(
        string operation,
        Func<CancellationToken, Task<HttpResponseMessage>> sendAsync,
        CancellationToken cancellationToken) =>
        TryReadAsync(
            operation,
            sendAsync,
            (response, maximumReadBytes, observeBytesRead, ct) =>
                CrumbSourceUtilities.ReadBoundedTextAsync(
                    response,
                    maximumReadBytes,
                    ct,
                    observeBytesRead),
            cancellationToken);

    private async Task<T?> TryReadAsync<T>(
        string operation,
        Func<CancellationToken, Task<HttpResponseMessage>> sendAsync,
        Func<HttpResponseMessage, int, Action<int>, CancellationToken, Task<T>> readAsync,
        CancellationToken cancellationToken)
        where T : class
    {
        var allowance = ClaimOperation(operation);
        if (allowance <= 0) return null;

        using var response = await sendAsync(cancellationToken);
        try
        {
            return await readAsync(
                response,
                SafeReadLimit(allowance, response.Content),
                ObserveBytesRead,
                cancellationToken);
        }
        catch (CrumbSourceResponseLimitExceededException)
        {
            RecordLimited(operation);
            return null;
        }
    }

    private int ClaimOperation(string operation)
    {
        if (remainingOperations <= 0)
        {
            RecordLimited(operation);
            return 0;
        }

        var allowance = RemainingBytes / remainingOperations;
        remainingOperations--;
        if (allowance <= 0)
        {
            RecordLimited(operation);
            return 0;
        }

        return allowance;
    }

    /// <summary>
    /// A chunked response needs one byte beyond its accepted body to prove overflow.
    /// Reserve that proof byte when this operation owns all remaining source capacity.
    /// </summary>
    private int SafeReadLimit(int allowance, HttpContent content) =>
        content.Headers.ContentLength is null && allowance >= RemainingBytes
            ? Math.Max(0, allowance - 1)
            : Math.Max(0, allowance);

    private int RemainingBytes => Math.Max(0, maximumBytes - consumedBytes);

    private void ObserveBytesRead(int count)
    {
        if (count <= 0) return;
        if (count > RemainingBytes)
        {
            consumedBytes = maximumBytes;
            throw new CrumbSourceResponseLimitExceededException();
        }

        consumedBytes += count;
    }

    private void RecordLimited(string operation)
    {
        var boundedOperation = operation.Length <= 80 ? operation : operation[..79] + "…";
        if (!limitedOperations.Contains(boundedOperation, StringComparer.Ordinal))
        {
            limitedOperations.Add(boundedOperation);
        }
    }
}
