import type { RecentPagerDutyIncident } from '../../api-client/types.gen'
import { formatTime } from '../../report/formatTime'

type RecentIncidentListProps = {
  incidents: RecentPagerDutyIncident[]
  loading: boolean
  onStart: (id: string) => Promise<void>
  triggerError: { id: string; message: string } | null
  triggeringId: string | null
}

export function RecentIncidentList({ incidents, loading, onStart, triggerError, triggeringId }: RecentIncidentListProps) {
  if (loading && incidents.length === 0) return <IncidentListSkeleton />
  if (incidents.length === 0) return <EmptyIncidentList />

  return (
    <div className={loading ? 'opacity-60' : ''} aria-busy={loading}>
      <div className="hidden grid-cols-[minmax(0,2.4fr)_minmax(9rem,1fr)_8rem_minmax(9rem,1fr)_9rem] gap-5 border-b border-border bg-muted/35 px-5 py-3 text-[11px] font-semibold uppercase tracking-wider text-muted-foreground md:grid">
        <span>Incident</span>
        <span>Service</span>
        <span>Status</span>
        <span>Opened</span>
        <span className="sr-only">Action</span>
      </div>
      <div className="divide-y divide-border">
        {incidents.map((incident) => {
          const rowError = triggerError?.id === incident.id ? triggerError.message : null
          const isStarting = triggeringId === incident.id
          return (
            <article key={incident.id} className="grid gap-4 px-4 py-5 md:grid-cols-[minmax(0,2.4fr)_minmax(9rem,1fr)_8rem_minmax(9rem,1fr)_9rem] md:items-center md:gap-5 md:px-5 md:py-4">
              <div className="min-w-0">
                <p className="mb-1 text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">
                  {incident.incidentNumber > 0 ? `Incident #${incident.incidentNumber}` : incident.id}
                  {incident.urgency.toLowerCase() === 'high' && <span className="ml-2 text-rose-600 dark:text-rose-300">High urgency</span>}
                </p>
                {incident.htmlUrl ? (
                  <a className="inline-flex max-w-full items-center gap-1.5 font-semibold text-foreground hover:underline" href={incident.htmlUrl} target="_blank" rel="noreferrer">
                    <span className="truncate">{incident.title}</span>
                    <ExternalLinkIcon />
                  </a>
                ) : (
                  <p className="truncate font-semibold text-foreground">{incident.title}</p>
                )}
                <p className="mt-1 truncate text-xs text-muted-foreground">{incident.id}</p>
              </div>

              <div className="min-w-0">
                <p className="text-xs font-medium text-muted-foreground md:hidden">Service</p>
                <p className="mt-0.5 truncate text-sm text-foreground md:mt-0">{incident.serviceName}</p>
                <p className="truncate text-xs text-muted-foreground">{incident.assignees.length > 0 ? incident.assignees.join(', ') : 'Unassigned'}</p>
              </div>

              <div>
                <p className="text-xs font-medium text-muted-foreground md:hidden">Status</p>
                <IncidentStatus status={incident.status} />
              </div>

              <div>
                <p className="text-xs font-medium text-muted-foreground md:hidden">Opened</p>
                <p className="mt-0.5 text-sm text-foreground md:mt-0" title={formatTime(incident.createdAt)}>{relativeTime(incident.createdAt)}</p>
                <p className="text-xs text-muted-foreground">Updated {relativeTime(incident.lastStatusChangeAt)}</p>
              </div>

              <div>
                <button
                  type="button"
                  className="inline-flex min-h-10 w-full items-center justify-center rounded-md bg-primary px-3.5 py-2 text-sm font-semibold text-primary-foreground shadow-sm hover:opacity-90 disabled:cursor-wait disabled:opacity-60"
                  disabled={triggeringId !== null || loading}
                  aria-describedby={rowError ? `trigger-error-${incident.id}` : undefined}
                  onClick={() => void onStart(incident.id)}
                >
                  {isStarting ? 'Starting…' : 'Investigate'}
                </button>
              </div>
              {rowError && <p id={`trigger-error-${incident.id}`} role="alert" className="text-xs leading-5 text-rose-600 dark:text-rose-300 md:col-span-5">{rowError}</p>}
            </article>
          )
        })}
      </div>
    </div>
  )
}

function IncidentStatus({ status }: { status: string }) {
  const normalized = status.toLowerCase()
  const tone = normalized === 'triggered'
    ? 'border-rose-500/25 bg-rose-500/10 text-rose-700 dark:text-rose-200'
    : normalized === 'acknowledged'
      ? 'border-amber-500/25 bg-amber-500/10 text-amber-700 dark:text-amber-200'
      : normalized === 'resolved'
        ? 'border-emerald-500/25 bg-emerald-500/10 text-emerald-700 dark:text-emerald-200'
        : 'border-border bg-secondary text-secondary-foreground'
  return <span className={`mt-1 inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-[11px] font-semibold capitalize md:mt-0 ${tone}`}><span className="status-dot bg-current opacity-70" />{normalized}</span>
}

function EmptyIncidentList() {
  return (
    <div className="grid min-h-72 place-items-center px-6 py-12 text-center">
      <div>
        <div className="mx-auto grid size-11 place-items-center rounded-full border border-border bg-muted/50 text-muted-foreground"><IncidentIcon /></div>
        <h2 className="mt-4 text-base font-semibold text-foreground">No incidents in this time frame</h2>
        <p className="mt-1 text-sm text-muted-foreground">Choose a longer period or refresh to pull the latest incidents.</p>
      </div>
    </div>
  )
}

function IncidentListSkeleton() {
  return (
    <div aria-label="Loading recent incidents" className="divide-y divide-border">
      {[0, 1, 2, 3].map((item) => (
        <div key={item} className="grid animate-pulse gap-4 px-5 py-5 md:grid-cols-[minmax(0,2.4fr)_minmax(9rem,1fr)_8rem_minmax(9rem,1fr)_9rem]">
          <div><div className="h-3 w-20 rounded bg-muted" /><div className="mt-3 h-4 max-w-80 rounded bg-muted" /></div>
          <div className="h-4 w-28 rounded bg-muted" />
          <div className="h-6 w-20 rounded-full bg-muted" />
          <div className="h-4 w-24 rounded bg-muted" />
          <div className="h-10 rounded bg-muted" />
        </div>
      ))}
    </div>
  )
}

function relativeTime(value: string) {
  const differenceSeconds = Math.round((new Date(value).getTime() - Date.now()) / 1000)
  const absoluteSeconds = Math.abs(differenceSeconds)
  const formatter = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' })
  if (absoluteSeconds < 60) return formatter.format(differenceSeconds, 'second')
  if (absoluteSeconds < 3600) return formatter.format(Math.round(differenceSeconds / 60), 'minute')
  if (absoluteSeconds < 86400) return formatter.format(Math.round(differenceSeconds / 3600), 'hour')
  return formatter.format(Math.round(differenceSeconds / 86400), 'day')
}

function ExternalLinkIcon() {
  return <svg aria-hidden="true" viewBox="0 0 24 24" className="size-3.5 shrink-0" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M15 3h6v6" /><path d="M10 14 21 3" /><path d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6" /></svg>
}

function IncidentIcon() {
  return <svg aria-hidden="true" viewBox="0 0 24 24" className="size-5" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><path d="M12 9v4" /><path d="M12 17h.01" /><path d="M10.3 3.7 2.2 18a2 2 0 0 0 1.7 3h16.2a2 2 0 0 0 1.7-3L13.7 3.7a2 2 0 0 0-3.4 0Z" /></svg>
}
