using Panko.Api.Domain;

namespace Panko.Api.Cases;

/// <summary>
/// Owns responder-visible Case progression. Policy decisions belong here rather than at each consumer.
/// </summary>
public static class CaseProgression
{
    public const string Queued = "queued";
    public const string Finalizing = "finalizing";
    public const string Collecting = "collecting";
    public const string Degraded = "degraded";
    public const string Ready = "ready";
    public const string Resolved = "resolved";
    public const string Open = "open";
    public const string Rebuilding = "rebuilding";
    public const string RefreshingSources = "refreshing-sources";
    public const string Analysing = "analysing";

    public static string ForDisabledCollection(bool isFrozen) => isFrozen ? Resolved : Ready;

    public static string ForCompletedCollection(
        bool isFrozen,
        IReadOnlyCollection<CrumbSourceStatus> sources)
    {
        if (isFrozen)
        {
            return Resolved;
        }

        return sources.All(source => source.Health == CrumbSourceHealth.Unavailable)
            ? Degraded
            : Ready;
    }

    public static bool CanRequestRebuild(string status) => status != Queued;

    public static bool NeedsStuckNotification(string status) => status == Collecting;

    public static string DisplayStatus(string persistedStatus, string caseFileStatus) =>
        persistedStatus == Queued ? "queued (rebuild requested)" : caseFileStatus;
}
