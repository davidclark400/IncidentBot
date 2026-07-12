import type { Report } from '../../incidentReport'
import { formatTime } from '../../report/formatTime'
import { Badge } from '../../report/ui'

export function IncidentReportHeader({ report, onReplayDemo }: { report: Report; onReplayDemo: () => Promise<void> }) {
  const isResolved = report.state.toLowerCase() === 'resolved'

  return (
    <header className="mb-5 grid gap-4 sm:mb-8 sm:gap-6 lg:grid-cols-[1fr_auto] lg:items-end">
      <div>
        <div className="mb-4 flex flex-wrap items-center gap-2">
          <Badge tone={isResolved ? 'success' : report.urgency === 'high' ? 'danger' : 'warning'}>{report.state}</Badge>
          <Badge>{report.urgency} urgency</Badge>
          <span className="font-mono text-xs text-muted-foreground">{report.pagerDutyIncidentId}</span>
        </div>
        <h1 className="max-w-4xl text-2xl font-semibold tracking-tight text-foreground sm:text-4xl">{report.title}</h1>
        <p className="mt-3 text-sm text-muted-foreground">
          {report.serviceId} <span className="mx-2 text-border">/</span> Triggered {formatTime(report.triggeredAt)}
        </p>
      </div>
      <div className="text-left text-xs text-muted-foreground lg:text-right">
        <p>Profile {report.profileId}</p>
        <p className="mt-1">Revision {report.profileRevision ?? 'loading'}</p>
        {report.pagerDutyIncidentId === 'PDEMO' && <button className="mt-3 rounded-md border border-border px-3 py-1.5 font-semibold text-foreground hover:bg-muted/50" onClick={() => void onReplayDemo()}>Replay demo</button>}
      </div>
    </header>
  )
}
