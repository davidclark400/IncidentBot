import { useEffect, useState } from 'react'
import { caseInputTypeLabel, type CaseInput, type PageOfCaseInput } from '../../case'
import { formatTime } from '../../case-file/formatTime'
import { Badge, type BadgeTone } from '../../case-file/ui'
import { getCaseInputs } from './caseInputApi'

const pageSize = 100

type CaseInputAuditProps = {
  caseId: string
  inputVersion: number
  projectedInputVersion: number
}

export function CaseInputAudit({ caseId, inputVersion, projectedInputVersion }: CaseInputAuditProps) {
  const [data, setData] = useState<PageOfCaseInput | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [loadingMore, setLoadingMore] = useState(false)
  const [reloadSequence, setReloadSequence] = useState(0)

  useEffect(() => {
    const controller = new AbortController()
    setLoading(true)
    setError(null)
    void getCaseInputs(caseId, 0, pageSize, controller.signal)
      .then((result) => {
        setData(result)
        setLoading(false)
      })
      .catch((reason: unknown) => {
        if (controller.signal.aborted) return
        setError(reason instanceof Error ? reason.message : 'Input history could not be loaded.')
        setLoading(false)
      })
    return () => controller.abort()
  }, [caseId, inputVersion, projectedInputVersion, reloadSequence])

  const events = data?.items ?? []
  async function loadMore() {
    if (!data || loadingMore || events.length >= data.total) return
    setLoadingMore(true)
    setError(null)
    try {
      const next = await getCaseInputs(caseId, events.length, pageSize)
      setData({ total: next.total, items: [...events, ...next.items] })
    } catch (reason: unknown) {
      setError(reason instanceof Error ? reason.message : 'More input history could not be loaded.')
    } finally {
      setLoadingMore(false)
    }
  }

  return (
    <section id="input-audit" className="surface mb-6 scroll-mt-28 overflow-hidden" aria-labelledby="input-audit-title">
      <div className="flex flex-col gap-2 border-b border-border px-4 py-4 sm:flex-row sm:items-end sm:justify-between sm:px-6">
        <div>
          <p className="eyebrow">Input audit</p>
          <h2 id="input-audit-title" className="mt-2 text-lg font-semibold text-foreground">Durable canonical inputs</h2>
        </div>
        <p className="text-xs text-muted-foreground">
          {data ? `${events.length} of ${data.total} inputs` : 'Loading input history…'}
        </p>
      </div>

      {error && (
        <div role="alert" className="flex flex-col gap-2 border-b border-rose-500/20 bg-rose-500/10 px-4 py-3 text-sm text-rose-700 dark:text-rose-200 sm:flex-row sm:items-center sm:justify-between sm:px-6">
          <span>{error}</span>
          <button type="button" className="font-semibold underline underline-offset-4" onClick={() => setReloadSequence((value) => value + 1)}>Try again</button>
        </div>
      )}

      {loading && events.length === 0 ? <AuditSkeleton /> : events.length === 0 && !error ? (
        <p className="px-4 py-10 text-center text-sm text-muted-foreground sm:px-6">No durable Case inputs are available yet.</p>
      ) : (
        <div className={loading ? 'opacity-60' : ''} aria-busy={loading}>
          <div className="hidden grid-cols-[4.5rem_minmax(0,1.7fr)_minmax(9rem,1fr)_minmax(11rem,1fr)_minmax(10rem,1fr)] gap-5 border-b border-border bg-muted/35 px-6 py-3 text-[11px] font-semibold uppercase tracking-wider text-muted-foreground lg:grid">
            <span>Sequence</span>
            <span>Input</span>
            <span>Producer</span>
            <span>Times</span>
            <span>State</span>
          </div>
          <div className="divide-y divide-border">
            {events.map((event) => <InputAuditRow event={event} projectedInputVersion={projectedInputVersion} key={event.id} />)}
          </div>
          {data && events.length < data.total && (
            <div className="border-t border-border px-4 py-4 text-center sm:px-6">
              <button
                type="button"
                className="inline-flex min-h-10 items-center justify-center rounded-md border border-border bg-background px-4 py-2 text-sm font-semibold text-foreground shadow-sm hover:bg-accent disabled:cursor-wait disabled:opacity-60"
                disabled={loadingMore}
                onClick={() => void loadMore()}
              >
                {loadingMore ? 'Loading…' : 'Load more inputs'}
              </button>
            </div>
          )}
        </div>
      )}
    </section>
  )
}

function InputAuditRow({ event, projectedInputVersion }: { event: CaseInput; projectedInputVersion: number }) {
  const state = eventState(event)
  return (
    <article className="grid gap-4 px-4 py-5 sm:px-6 lg:grid-cols-[4.5rem_minmax(0,1.7fr)_minmax(9rem,1fr)_minmax(11rem,1fr)_minmax(10rem,1fr)] lg:items-start lg:gap-5 lg:py-4">
      <div>
        <p className="text-xs font-medium text-muted-foreground lg:hidden">Sequence</p>
        <p className="font-mono text-sm font-semibold text-foreground">#{event.sequence}</p>
        <p className="mt-1 font-mono text-[10px] text-muted-foreground">input v{event.inputVersion}</p>
      </div>

      <div className="min-w-0">
        <div className="flex flex-wrap items-center gap-2">
          <Badge>{caseInputTypeLabel(event.kind)}</Badge>
          <span className="text-xs text-muted-foreground">{event.category} · {event.severity}</span>
        </div>
        <p className="mt-2 text-sm leading-6 text-foreground">{event.summary}</p>
        <p className="mt-1 break-all font-mono text-[10px] text-muted-foreground">Client Crumb {event.clientCrumbId}</p>
      </div>

      <div className="min-w-0">
        <p className="text-xs font-medium text-muted-foreground lg:hidden">Producer</p>
        <p className="break-words text-sm text-foreground">{event.producerPrincipal}</p>
        <div className="mt-2"><Badge tone={event.trustLevel.toLowerCase() === 'submitted' ? 'warning' : 'default'}>{event.trustLevel}</Badge></div>
        {event.declaredSource && <p className="mt-1.5 text-xs text-muted-foreground">Declared source: {event.declaredSource}</p>}
      </div>

      <div className="text-xs text-muted-foreground">
        <p><span className="font-medium text-foreground">Occurred</span><br /><time dateTime={event.occurredAt}>{formatTime(event.occurredAt)}</time></p>
        <p className="mt-2"><span className="font-medium text-foreground">Received</span><br /><time dateTime={event.receivedAt}>{formatTime(event.receivedAt)}</time></p>
      </div>

      <div>
        <Badge tone={state.tone}>{state.label}</Badge>
        <p className="mt-2 text-xs leading-5 text-muted-foreground">{projectionLabel(event, projectedInputVersion)}</p>
        {event.supersedesCrumbId && <p className="mt-1 font-mono text-[10px] text-muted-foreground">Supersedes {event.supersedesCrumbId.slice(0, 8)}</p>}
        {event.supersededByCrumbId && <p className="mt-1 font-mono text-[10px] text-muted-foreground">Superseded by {event.supersededByCrumbId.slice(0, 8)}</p>}
        {event.retractedAt && <p className="mt-1 text-[10px] text-muted-foreground">Retracted {formatTime(event.retractedAt)}</p>}
      </div>
    </article>
  )
}

function eventState(event: CaseInput): { label: string; tone: BadgeTone } {
  if (event.supersededByCrumbId) return { label: 'Superseded', tone: 'warning' }
  if (event.retractedAt) return { label: 'Retracted', tone: 'danger' }
  if (!event.active) return { label: 'Inactive', tone: 'warning' }
  return { label: 'Active', tone: 'success' }
}

function projectionLabel(event: CaseInput, projectedInputVersion: number) {
  if (event.projectedInInputVersion !== null) return `Projected in input v${event.projectedInInputVersion}`
  if (event.active && event.inputVersion > projectedInputVersion) return 'Awaiting projection'
  if (!event.active) return 'Excluded from the current projection'
  return `Not present in projected input v${projectedInputVersion}`
}

function AuditSkeleton() {
  return (
    <div aria-label="Loading input history" className="divide-y divide-border">
      {[0, 1, 2].map((item) => (
        <div key={item} className="grid animate-pulse gap-4 px-6 py-5 lg:grid-cols-[4.5rem_minmax(0,1.7fr)_minmax(9rem,1fr)_minmax(11rem,1fr)_minmax(10rem,1fr)]">
          <div className="h-4 w-12 rounded bg-muted" />
          <div><div className="h-4 max-w-80 rounded bg-muted" /><div className="mt-3 h-3 w-28 rounded bg-muted" /></div>
          <div className="h-4 w-24 rounded bg-muted" />
          <div className="h-4 w-32 rounded bg-muted" />
          <div className="h-6 w-20 rounded-full bg-muted" />
        </div>
      ))}
    </div>
  )
}
