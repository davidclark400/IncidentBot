import { formatTime } from '../../case-file/formatTime'
import type { CatalogScope } from '../catalog/catalogModel'
import { CaseList } from './CaseList'
import { RecentPagerDutyIncidentList } from './RecentPagerDutyIncidentList'
import { useCaseActivity } from './useCaseActivity'

const timeFrames = [
  { hours: 6, label: 'Last 6 hours' },
  { hours: 24, label: 'Last 24 hours' },
  { hours: 72, label: 'Last 3 days' },
  { hours: 168, label: 'Last 7 days' },
  { hours: 720, label: 'Last 30 days' },
]

export function CaseActivity({ scope }: { scope: CatalogScope }) {
  const {
    data,
    error,
    hours,
    loading,
    refresh,
    cases,
    casesError,
    casesLoading,
    setHours,
    openCase,
    openError,
    openingId,
  } = useCaseActivity(scope)
  const pagerDutyIncidents = data?.incidents ?? []
  const recentCases = cases?.cases ?? []
  const refreshing = loading || casesLoading

  return (
    <section className="mt-12 border-t border-border pt-10" aria-labelledby="recent-cases-heading">
        <div className="flex flex-col gap-7 sm:flex-row sm:items-end sm:justify-between">
          <div className="max-w-2xl">
            <p className="eyebrow">Activity</p>
            <h2 id="recent-cases-heading" className="mt-3 text-2xl font-semibold tracking-tight text-foreground sm:text-3xl">Recent Cases</h2>
            <p className="mt-3 max-w-xl text-sm leading-6 text-muted-foreground">Durable Cases and PagerDuty intake for {scope.label}.</p>
          </div>
          <button
            type="button"
            className="inline-flex min-h-10 items-center justify-center gap-2 rounded-md border border-border bg-background px-3.5 py-2 text-sm font-semibold text-foreground shadow-sm hover:bg-accent disabled:cursor-wait disabled:opacity-60"
            disabled={refreshing}
            onClick={refresh}
          >
            <RefreshIcon spinning={refreshing} />
            {refreshing ? 'Refreshing…' : 'Refresh'}
          </button>
        </div>

        {casesError && (
          <div role="alert" className="mt-7 flex flex-col gap-3 rounded-md border border-rose-500/25 bg-rose-500/10 px-4 py-3 text-sm text-rose-700 dark:text-rose-200 sm:flex-row sm:items-center sm:justify-between">
            <span>{casesError}</span>
            <button type="button" className="font-semibold underline underline-offset-4" onClick={refresh}>Try again</button>
          </div>
        )}

        <section className="surface mt-8 overflow-hidden" aria-labelledby="case-list-heading">
          <div className="flex flex-col gap-2 border-b border-border px-4 py-4 sm:flex-row sm:items-center sm:justify-between sm:px-5">
            <div className="flex items-center gap-2.5">
              <span className="status-dot bg-emerald-500" />
              <h2 id="case-list-heading" className="text-sm font-semibold text-foreground">Persisted Cases</h2>
              {!casesLoading && <span className="rounded-full bg-secondary px-2 py-0.5 text-xs font-semibold text-secondary-foreground">{cases?.total ?? recentCases.length}</span>}
            </div>
            <p className="text-xs text-muted-foreground">Both agent and PagerDuty origins</p>
          </div>
          <CaseList cases={recentCases} loading={casesLoading} scopeLabel={scope.label} />
        </section>

        <div className="mt-12 flex flex-col gap-5 lg:flex-row lg:items-end lg:justify-between">
          <div className="max-w-2xl">
            <p className="eyebrow">Intake adapter</p>
            <h2 className="mt-3 text-2xl font-semibold tracking-tight text-foreground sm:text-3xl">PagerDuty intake</h2>
            <p className="mt-3 max-w-xl text-sm leading-6 text-muted-foreground">Pull recent PagerDuty incidents and open or reopen their durable Case.</p>
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
              {loading ? 'Pulling…' : 'Pull now'}
            </button>
          </div>
        </div>

        {error && (
          <div role="alert" className="mt-7 flex flex-col gap-3 rounded-md border border-rose-500/25 bg-rose-500/10 px-4 py-3 text-sm text-rose-700 dark:text-rose-200 sm:flex-row sm:items-center sm:justify-between">
            <span>{error}</span>
            <button type="button" className="font-semibold underline underline-offset-4" onClick={refresh}>Try again</button>
          </div>
        )}

        <section className="surface mt-6 overflow-hidden" aria-labelledby="pagerduty-incident-list-heading">
          <div className="flex flex-col gap-2 border-b border-border px-4 py-4 sm:flex-row sm:items-center sm:justify-between sm:px-5">
            <div className="flex items-center gap-2.5">
              <span className="status-dot bg-emerald-500" />
              <h2 id="pagerduty-incident-list-heading" className="text-sm font-semibold text-foreground">PagerDuty incidents</h2>
              {!loading && <span className="rounded-full bg-secondary px-2 py-0.5 text-xs font-semibold text-secondary-foreground">{pagerDutyIncidents.length}</span>}
            </div>
            {data && <p className="text-xs text-muted-foreground">Pulled through {formatTime(data.until)}</p>}
          </div>

          <RecentPagerDutyIncidentList
            incidents={pagerDutyIncidents}
            loading={loading}
            onOpen={openCase}
            scopeLabel={scope.label}
            openError={openError}
            openingId={openingId}
          />

          {data?.hasMore && (
            <p className="border-t border-border bg-muted/30 px-5 py-3 text-xs text-muted-foreground">PagerDuty has more authorized incidents in this period. Narrow the time frame to see the most relevant results for this scope.</p>
          )}
        </section>

        <p className="mt-4 text-xs leading-5 text-muted-foreground">Cases use the PagerDuty incident’s current state and the allowlisted Recipe for its service. Opening the same unchanged event again returns to the existing Case.</p>
    </section>
  )
}

function RefreshIcon({ spinning }: { spinning: boolean }) {
  return <svg aria-hidden="true" viewBox="0 0 24 24" className={`size-4 ${spinning ? 'animate-spin' : ''}`} fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M20 11a8.1 8.1 0 0 0-15.5-2M4 4v5h5" /><path d="M4 13a8.1 8.1 0 0 0 15.5 2M20 20v-5h-5" /></svg>
}
