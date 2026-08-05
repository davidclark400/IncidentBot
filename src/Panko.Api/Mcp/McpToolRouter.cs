using System.Text.Json;
using Panko.Api.Cases;
using Panko.Api.Options;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace Panko.Api.Mcp;

/// <summary>
/// Applies the common safety policy around inbound MCP tool calls. This type deliberately
/// retains no transport or Case state.
/// </summary>
public sealed class McpToolRouter(
    IOptions<CaseOptions> options,
    CaseTelemetry telemetry,
    ILogger<McpToolRouter> logger)
{
    private const int MaximumValidationErrorCharacters = 512;

    public async Task<T> InvokeAsync<T>(
        string tool,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        telemetry.McpCommand(tool);
        try
        {
            var result = await operation(cancellationToken);
            return EnsureBounded(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CaseValidationException exception)
        {
            telemetry.McpFailure(tool);
            throw new McpException(BoundedValidationMessage(exception.Message));
        }
        catch (CaseAuthorizationException)
        {
            telemetry.McpFailure(tool);
            throw new McpException("The caller is not authorized to perform this operation.");
        }
        catch (CaseNotFoundException)
        {
            telemetry.McpFailure(tool);
            throw new McpException("The Case was not found or is not accessible.");
        }
        catch (CaseConflictException)
        {
            telemetry.McpFailure(tool);
            throw new McpException(
                "The command conflicts with the current Case state or an earlier idempotent request.");
        }
        catch (McpException)
        {
            telemetry.McpFailure(tool);
            throw;
        }
        catch (Exception exception)
        {
            telemetry.McpFailure(tool);
            logger.LogError(exception, "Inbound MCP tool {Tool} failed", tool);
            throw new McpException("Panko could not complete the tool call.");
        }
    }

    private T EnsureBounded<T>(T result)
    {
        var maximumBytes = options.Value.MaximumMcpResponseBytes;
        if (Fits(result, maximumBytes))
        {
            return result;
        }

        if (result is McpGetCaseResult status)
        {
            return (T)(object)BoundSummary(status, maximumBytes);
        }

        throw new McpException("The tool result exceeded the configured response limit.");
    }

    private static McpGetCaseResult BoundSummary(
        McpGetCaseResult status,
        int maximumBytes)
    {
        var withoutSummary = status with { DeterministicSummary = null };
        if (!Fits(withoutSummary, maximumBytes))
        {
            throw new McpException("The tool result exceeded the configured response limit.");
        }

        var summary = status.DeterministicSummary;
        if (string.IsNullOrEmpty(summary))
        {
            return withoutSummary;
        }

        var low = 0;
        var high = summary.Length;
        var best = withoutSummary;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var prefixLength = AvoidSplittingSurrogatePair(summary, middle);
            var candidate = status with
            {
                DeterministicSummary = prefixLength == summary.Length
                    ? summary
                    : string.Concat(summary.AsSpan(0, prefixLength), "\u2026")
            };
            if (Fits(candidate, maximumBytes))
            {
                best = candidate;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return best;
    }

    private static int AvoidSplittingSurrogatePair(string value, int length) =>
        length > 0 && length < value.Length
            && char.IsHighSurrogate(value[length - 1])
            && char.IsLowSurrogate(value[length])
                ? length - 1
                : length;

    private static bool Fits<T>(T result, int maximumBytes) =>
        JsonSerializer.SerializeToUtf8Bytes(result, McpServerJson.Options).Length <= maximumBytes;

    private static string BoundedValidationMessage(string message)
    {
        var safeMessage = string.IsNullOrWhiteSpace(message)
            ? "The tool input is invalid."
            : message.Trim();
        if (safeMessage.Length > MaximumValidationErrorCharacters)
        {
            safeMessage = string.Concat(
                safeMessage.AsSpan(0, MaximumValidationErrorCharacters - 1),
                "\u2026");
        }
        return $"Invalid tool input: {safeMessage}";
    }
}
