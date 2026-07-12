namespace IncidentBot.Api.Connectors;

/// <summary>
/// Enforces one cumulative response-body allowance across a connector collection.
/// Each planned request initially receives an equal share of the bytes that remain;
/// bytes unused by earlier requests remain available to later requests.
/// </summary>
internal sealed class ConnectorByteBudget
{
    private readonly List<string> limitedOperations = [];
    private int remainingOperations;

    public ConnectorByteBudget(int scopeMaximumBytes, int connectorMaximumBytes, int plannedOperations)
    {
        MaximumBytes = Math.Min(
            Math.Max(0, scopeMaximumBytes),
            Math.Max(0, connectorMaximumBytes));
        remainingOperations = Math.Max(0, plannedOperations);
    }

    public int MaximumBytes { get; }

    public int ConsumedBytes { get; private set; }

    public int RemainingBytes => Math.Max(0, MaximumBytes - ConsumedBytes);

    public bool IsPartial => limitedOperations.Count > 0;

    public string? Diagnostic => IsPartial
        ? ConnectorUtilities.CombineDiagnostics(
            $"Cumulative source response byte budget constrained collection: used {ConsumedBytes} of "
            + $"{MaximumBytes} bytes; limited {limitedOperations.Count} request(s): "
            + string.Join(", ", limitedOperations.Take(6))
            + (limitedOperations.Count > 6 ? ", …" : ""))
        : null;

    /// <summary>
    /// Claims a fair share for the next planned operation. The operation is removed
    /// from the plan immediately so that unused bytes flow into subsequent claims.
    /// </summary>
    public int BeginOperation(string operation)
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
    /// Removes a conditional request that is no longer needed, allowing its reserved
    /// unused capacity to be shared by the remaining work.
    /// </summary>
    public void RemovePlannedOperation()
    {
        if (remainingOperations > 0) remainingOperations--;
    }

    /// <summary>
    /// For a chunked response, reserve the final byte for the bounded reader's
    /// one-byte overflow probe so actual reads never cross the source-wide cap.
    /// </summary>
    public int SafeReadLimit(int allowance, HttpContent content) =>
        content.Headers.ContentLength is null && allowance >= RemainingBytes
            ? Math.Max(0, allowance - 1)
            : Math.Max(0, allowance);

    public void ObserveBytesRead(int count)
    {
        if (count <= 0) return;
        if (count > RemainingBytes)
        {
            ConsumedBytes = MaximumBytes;
            throw new InvalidOperationException("Connector response exceeded its configured byte limit.");
        }

        ConsumedBytes += count;
    }

    public void RecordLimited(string operation)
    {
        var boundedOperation = operation.Length <= 80 ? operation : operation[..79] + "…";
        if (!limitedOperations.Contains(boundedOperation, StringComparer.Ordinal))
        {
            limitedOperations.Add(boundedOperation);
        }
    }
}
