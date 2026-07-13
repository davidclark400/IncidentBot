import { ThemeToggle } from '../../components/ThemeToggle'
import type { ThemeControl } from '../../hooks/useTheme'
import { formatTime } from '../../report/formatTime'
import { RecentIncidentList } from './RecentIncidentList'
import { useRecentIncidents } from './useRecentIncidents'

const timeFrames = [
  { hours: 6, label: 'Last 6 hours' },
  { hours: 24, label: 'Last 24 hours' },
  { hours: 72, label: 'Last 3 days' },
  { hours: 168, label: 'Last 7 days' },
  { hours: 720, label: 'Last 30 days' },
]

export function RecentIncidentsPage({ theme }: { theme: ThemeControl }) {
  const {
    data,
    error,
    hours,
    loading,
    refresh,
    setHours,
    startInvestigation,
    triggerError,
    triggeringId,
  } = useRecentIncidents()
  const incidents = data?.incidents ?? []

  return (
    <div className="min-h-screen bg-background text-foreground">
      <header className="sticky top-0 z-40 border-b border-border bg-background/90 backdrop-blur-xl">
        <div className="mx-auto flex min-h-16 max-w-7xl items-center justify-between px-4 py-2.5 sm:px-5 lg:px-8">
          <div className="flex items-center gap-3">
            <div className="grid size-8 place-items-center rounded-md bg-primary text-sm font-semibold text-primary-foreground">IB</div>
            <div>
              <p className="text-sm font-semibold leading-none text-foreground">Incident Bot</p>
              <p className="mt-1 hidden text-[11px] text-muted-foreground sm:block">PagerDuty investigations</p>
            </div>
          </div>
          <ThemeToggle {...theme} />
        </div>
      </header>

      <main className="mx-auto max-w-7xl px-4 py-10 sm:px-5 sm:py-14 lg:px-8">
        <div className="flex flex-col gap-7 lg:flex-row lg:items-end lg:justify-between">
          <div className="max-w-2xl">
            <p className="eyebrow">PagerDuty</p>
            <h1 className="mt-3 text-3xl font-semibold tracking-tight text-foreground sm:text-4xl">Recent incidents</h1>
            <p className="mt-3 max-w-xl text-sm leading-6 text-muted-foreground sm:text-base">Pull incidents from PagerDuty, choose the relevant event, and start a scoped investigation on demand.</p>
          </div>

          <div className="flex flex-col gap-3 sm:flex-row sm:items-end">
            <label className="block">
              <span className="mb-1.5 block text-xs font-semibold text-foreground">Time frame</span>
              <select
                className="min-h-10 w-full rounded-md border border-input bg-background px-3 py-2 text-sm text-foreground shadow-sm outline-none focus:border-ring focus:ring-2 focus:ring-ring/20 sm:w-44"
                value={hours}
                onChange={(event) => setHours(Number(event.target.value))}
              >
                {timeFrames.map((timeFrame) => <option key={timeFrame.hours} value={timeFrame.hours}>{timeFrame.label}</option>)}
              </select>
            </label>
            <button
              type="button"
              className="inline-flex min-h-10 items-center justify-center gap-2 rounded-md border border-border bg-background px-3.5 py-2 text-sm font-semibold text-foreground shadow-sm hover:bg-accent disabled:cursor-wait disabled:opacity-60"
              disabled={loading}
              onClick={refresh}
            >
              <RefreshIcon spinning={loading} />
              {loading ? 'Pulling…' : 'Refresh'}
            </button>
          </div>
        </div>

        {error && (
          <div role="alert" className="mt-7 flex flex-col gap-3 rounded-md border border-rose-500/25 bg-rose-500/10 px-4 py-3 text-sm text-rose-700 dark:text-rose-200 sm:flex-row sm:items-center sm:justify-between">
            <span>{error}</span>
            <button type="button" className="font-semibold underline underline-offset-4" onClick={refresh}>Try again</button>
          </div>
        )}

        <section className="surface mt-8 overflow-hidden" aria-labelledby="incident-list-heading">
          <div className="flex flex-col gap-2 border-b border-border px-4 py-4 sm:flex-row sm:items-center sm:justify-between sm:px-5">
            <div className="flex items-center gap-2.5">
              <span className="status-dot bg-emerald-500" />
              <h2 id="incident-list-heading" className="text-sm font-semibold text-foreground">PagerDuty incidents</h2>
              {!loading && <span className="rounded-full bg-secondary px-2 py-0.5 text-xs font-semibold text-secondary-foreground">{incidents.length}</span>}
            </div>
            {data && <p className="text-xs text-muted-foreground">Pulled through {formatTime(data.until)}</p>}
          </div>

          <RecentIncidentList
            incidents={incidents}
            loading={loading}
            onStart={startInvestigation}
            triggerError={triggerError}
            triggeringId={triggeringId}
          />

          {data?.hasMore && (
            <p className="border-t border-border bg-muted/30 px-5 py-3 text-xs text-muted-foreground">PagerDuty has more incidents in this period. Narrow the time frame to see the most relevant results.</p>
          )}
        </section>

        <p className="mt-4 text-xs leading-5 text-muted-foreground">Investigations use the incident’s current PagerDuty state and the allowlisted profile for its service. Starting the same unchanged event again opens the existing investigation.</p>
      </main>
    </div>
  )
}

function RefreshIcon({ spinning }: { spinning: boolean }) {
  return <svg aria-hidden="true" viewBox="0 0 24 24" className={`size-4 ${spinning ? 'animate-spin' : ''}`} fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M20 11a8.1 8.1 0 0 0-15.5-2M4 4v5h5" /><path d="M4 13a8.1 8.1 0 0 0 15.5 2M20 20v-5h-5" /></svg>
}
