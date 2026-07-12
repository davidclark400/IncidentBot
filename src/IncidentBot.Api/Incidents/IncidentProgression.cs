using IncidentBot.Api.Domain;

namespace IncidentBot.Api.Incidents;

/// <summary>
/// Owns responder-visible investigation progression. Status remains a string in persisted reports
/// for backwards compatibility, but policy decisions belong here rather than at each consumer.
/// </summary>
public static class IncidentProgression
{
    public const string Queued = "queued";
    public const string Finalizing = "finalizing";
    public const string Collecting = "collecting";
    public const string Degraded = "degraded";
    public const string Ready = "ready";
    public const string Resolved = "resolved";

    public static string ForDisabledCollection(bool isFrozen) => isFrozen ? Resolved : Ready;

    public static string ForCompletedCollection(
        bool isFrozen,
        IReadOnlyCollection<SourceReport> sources)
    {
        if (isFrozen)
        {
            return Resolved;
        }

        return sources.All(source => source.Health == SourceHealth.Unavailable)
            ? Degraded
            : Ready;
    }

    public static bool CanRequestRestart(string status) => status != Queued;

    public static bool NeedsStuckNotification(string status) => status == Collecting;

    public static string DisplayStatus(string persistedStatus, string reportStatus) =>
        persistedStatus == Queued ? "queued (restart requested)" : reportStatus;
}
