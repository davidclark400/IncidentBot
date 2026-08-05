import type { RecentCase } from '../../case'
import { formatTime } from '../../case-file/formatTime'
import { Badge, type BadgeTone } from '../../case-file/ui'
import { relativeTime } from './relativeTime'

type CaseListProps = {
  cases: RecentCase[]
  loading: boolean
  scopeLabel: string
}

export function CaseList({ cases, loading, scopeLabel }: CaseListProps) {
  if (loading && cases.length === 0) return <CaseListSkeleton />
  if (cases.length === 0) return <EmptyCaseList scopeLabel={scopeLabel} />

  return (
    <div className={loading ? 'opacity-60' : ''} aria-busy={loading}>
      <div className="hidden grid-cols-[minmax(0,2fr)_minmax(10rem,1.1fr)_8.5rem_minmax(10rem,1fr)_minmax(10rem,1fr)] gap-5 border-b border-border bg-muted/35 px-5 py-3 text-[11px] font-semibold uppercase tracking-wider text-muted-foreground lg:grid">
        <span>Case</span>
        <span>Recipe / service</span>
        <span>Status</span>
        <span>Versions</span>
        <span>Created / updated</span>
      </div>
      <div className="divide-y divide-border">
        {cases.map((caseItem) => <CaseRow caseItem={caseItem} key={caseItem.caseId} />)}
      </div>
    </div>
  )
}

function CaseRow({ caseItem }: { caseItem: RecentCase }) {
  const lagging = caseItem.inputVersion > caseItem.projectedInputVersion
  return (
    <article className="grid gap-4 px-4 py-5 sm:px-5 lg:grid-cols-[minmax(0,2fr)_minmax(10rem,1.1fr)_8.5rem_minmax(10rem,1fr)_minmax(10rem,1fr)] lg:items-center lg:gap-5 lg:py-4">
      <div className="min-w-0">
        <div className="mb-2 flex flex-wrap items-center gap-2">
          <Badge tone={originTone(caseItem.origin)}>{originLabel(caseItem.origin)}</Badge>
          <span className="truncate font-mono text-[10px] text-muted-foreground">{caseItem.caseId}</span>
        </div>
        <a className="inline-flex max-w-full items-center gap-1.5 font-semibold text-foreground hover:underline" href={caseItem.caseUrl}>
          <span className="truncate">{caseItem.title}</span>
          <ArrowIcon />
        </a>
      </div>

      <div className="min-w-0">
        <p className="text-xs font-medium text-muted-foreground lg:hidden">Recipe / service</p>
        <p className="mt-0.5 truncate text-sm text-foreground lg:mt-0">{caseItem.recipeId}</p>
        <p className="truncate text-xs text-muted-foreground">{caseItem.serviceId}</p>
      </div>

      <div>
        <p className="text-xs font-medium text-muted-foreground lg:hidden">Status</p>
        <CaseStatus status={caseItem.status} />
        {lagging && <p className="mt-1.5 text-[10px] font-semibold leading-4 text-amber-700 dark:text-amber-200">Rebuilding from new inputs</p>}
      </div>

      <div className="text-xs text-muted-foreground">
        <p className="font-medium text-foreground">Input v{caseItem.inputVersion}</p>
        <p className={lagging ? 'font-semibold text-amber-700 dark:text-amber-200' : ''}>Projected v{caseItem.projectedInputVersion}</p>
        <p>Case File v{caseItem.caseFileVersion}</p>
      </div>

      <div className="min-w-0">
        <p className="text-xs font-medium text-muted-foreground lg:hidden">Created / updated</p>
        <p className="mt-0.5 truncate text-sm text-foreground lg:mt-0">{caseItem.createdBy || defaultCreator(caseItem.origin)}</p>
        <p className="text-xs text-muted-foreground" title={formatTime(caseItem.updatedAt)}>Updated {relativeTime(caseItem.updatedAt)}</p>
      </div>
    </article>
  )
}

function CaseStatus({ status }: { status: string }) {
  const normalized = status.toLowerCase()
  const tone = normalized === 'ready' || normalized === 'completed' || normalized === 'resolved'
    ? 'border-emerald-500/25 bg-emerald-500/10 text-emerald-700 dark:text-emerald-200'
    : normalized === 'failed'
      ? 'border-rose-500/25 bg-rose-500/10 text-rose-700 dark:text-rose-200'
      : normalized === 'rebuilding' || normalized === 'queued' || normalized === 'collecting' || normalized === 'synthesizing'
        ? 'border-amber-500/25 bg-amber-500/10 text-amber-700 dark:text-amber-200'
        : 'border-border bg-secondary text-secondary-foreground'
  return <span className={`mt-1 inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-[11px] font-semibold capitalize lg:mt-0 ${tone}`}><span className="status-dot bg-current opacity-70" />{normalized}</span>
}

function originLabel(origin: string) {
  if (origin.toLowerCase() === 'pagerduty') return 'PagerDuty'
  if (origin.toLowerCase() === 'agent') return 'Agent'
  return 'Manual'
}

function originTone(origin: string): BadgeTone {
  return origin.toLowerCase() === 'agent' ? 'warning' : 'default'
}

function defaultCreator(origin: string) {
  if (origin.toLowerCase() === 'pagerduty') return 'PagerDuty adapter'
  if (origin.toLowerCase() === 'manual') return 'Manual intake'
  return 'Unknown producer'
}

function EmptyCaseList({ scopeLabel }: { scopeLabel: string }) {
  return (
    <div className="grid min-h-56 place-items-center px-6 py-12 text-center">
      <div>
        <div className="mx-auto grid size-11 place-items-center rounded-full border border-border bg-muted/50 text-muted-foreground"><CaseIcon /></div>
        <h2 className="mt-4 text-base font-semibold text-foreground">No Cases for {scopeLabel}</h2>
        <p className="mt-1 text-sm text-muted-foreground">Agent and PagerDuty Cases for this scope will appear here after intake.</p>
      </div>
    </div>
  )
}

function CaseListSkeleton() {
  return (
    <div aria-label="Loading recent Cases" className="divide-y divide-border">
      {[0, 1, 2].map((item) => (
        <div key={item} className="grid animate-pulse gap-4 px-5 py-5 lg:grid-cols-[minmax(0,2fr)_minmax(10rem,1.1fr)_8.5rem_minmax(10rem,1fr)_minmax(10rem,1fr)]">
          <div><div className="h-5 w-24 rounded-full bg-muted" /><div className="mt-3 h-4 max-w-80 rounded bg-muted" /></div>
          <div className="h-4 w-28 rounded bg-muted" />
          <div className="h-6 w-20 rounded-full bg-muted" />
          <div className="h-10 w-24 rounded bg-muted" />
          <div className="h-9 w-32 rounded bg-muted" />
        </div>
      ))}
    </div>
  )
}

function ArrowIcon() {
  return <svg aria-hidden="true" viewBox="0 0 24 24" className="size-3.5 shrink-0" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="m9 18 6-6-6-6" /></svg>
}

function CaseIcon() {
  return <svg aria-hidden="true" viewBox="0 0 24 24" className="size-5" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><circle cx="12" cy="12" r="9" /><path d="M8 12h8M12 8v8" /></svg>
}
