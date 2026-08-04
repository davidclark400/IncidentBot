import { AnimatePresence } from 'motion/react'
import { useMemo } from 'react'
import { caseInputIdForTrailEntry, crumbSubmissionMetadata, type Crumb, type TrailEntry } from '../caseFile'
import { formatTime } from './formatTime'
import { Badge, CodeReferenceLink, LiveItem } from './ui'

export function CrumbTrailReview({ trail, crumbs }: { trail: TrailEntry[]; crumbs: Crumb[] }) {
  const declaredSources = useMemo(() => {
    const sources = new Map<string, string | null>()
    for (const crumb of crumbs) {
      const metadata = crumbSubmissionMetadata(crumb)
      if (!metadata.submitted) continue
      if (metadata.caseInputId) sources.set(normalizeCaseInputId(metadata.caseInputId), metadata.declaredSource)
    }
    return sources
  }, [crumbs])

  return (
    <section className="mb-6 grid gap-4 xl:grid-cols-[1fr_1.2fr] xl:gap-6">
      <article id="trail" className="surface min-w-0 scroll-mt-28 p-4 sm:p-6">
        <div className="flex items-center justify-between">
          <div><p className="eyebrow">Trail</p><h2 className="mt-2 text-lg font-semibold text-foreground">What changed</h2></div>
          <span className="text-xs text-muted-foreground">UTC</span>
        </div>
        <div className="relative mt-5 space-y-0 sm:mt-6">
          {trail.length === 0 && <p className="text-sm text-muted-foreground">Waiting for Trail events…</p>}
          <AnimatePresence initial={false} mode="popLayout">
            {trail.map((item, index) => <LiveItem key={item.id || fallbackTrailKey(item)}><TrailRow item={item} last={index === trail.length - 1} declaredSource={declaredSourceForTrail(item, declaredSources)} /></LiveItem>)}
          </AnimatePresence>
        </div>
      </article>

      <article id="crumbs" className="surface min-w-0 scroll-mt-28 p-4 sm:p-6">
        <div className="flex items-center justify-between gap-3">
          <div><p className="eyebrow">Crumbs</p><h2 className="mt-2 text-lg font-semibold text-foreground">Highest-signal crumbs</h2></div>
          <span className="shrink-0 text-xs text-muted-foreground">
            {crumbs.length > 25 ? `Top 25 of ${crumbs.length} crumbs` : `${crumbs.length} crumbs`}
          </span>
        </div>
        <div className="relative mt-5 space-y-3">
          {crumbs.length === 0 && <p className="text-sm text-muted-foreground">Collectors are querying the allowlisted sources…</p>}
          <AnimatePresence initial={false} mode="popLayout">
            {crumbs.slice(0, 25).map((crumb) => <LiveItem key={crumb.id}><CrumbCard crumb={crumb} /></LiveItem>)}
          </AnimatePresence>
        </div>
      </article>
    </section>
  )
}

export function LogErrorSection({ crumbs }: { crumbs: Crumb[] }) {
  const errors = useMemo(
    () => crumbs
      .filter((crumb) => crumb.source.toLowerCase() === 'victorialogs' && ['first-error', 'log-sample'].includes(crumb.category))
      .sort((a, b) => new Date(a.occurredAt).getTime() - new Date(b.occurredAt).getTime()),
    [crumbs],
  )
  if (errors.length === 0) return null
  return (
    <section id="log-errors" className="surface mb-6 scroll-mt-28 p-4 sm:p-6">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div><p className="eyebrow">Summarised log error lines</p><h2 className="mt-2 text-lg font-semibold text-foreground">Errors around the case window</h2></div>
        <span className="text-xs text-muted-foreground">{errors.length} sampled line{errors.length === 1 ? '' : 's'} · UTC</span>
      </div>
      <div className="relative mt-5 divide-y divide-border rounded-xl border border-border bg-muted/30">
        <AnimatePresence initial={false} mode="popLayout">
          {errors.slice(0, 20).map((crumb, index) => <LiveItem key={crumb.id}><LogErrorRow crumb={crumb} first={index === 0} /></LiveItem>)}
        </AnimatePresence>
      </div>
    </section>
  )
}

function TrailRow({ item, last, declaredSource }: { item: TrailEntry; last: boolean; declaredSource: string | null }) {
  const submitted = item.source.toLowerCase() === 'submitted'
  const content = <><div className="flex flex-wrap items-center gap-2"><p className="text-sm leading-6 text-foreground">{item.summary}</p>{submitted && <Badge tone="warning">Submitted by agent</Badge>}</div><div className="mt-1.5 flex flex-wrap items-center gap-x-2 gap-y-1 text-xs text-muted-foreground"><time dateTime={item.occurredAt}>{formatTime(item.occurredAt)}</time><span aria-hidden="true">·</span><span className="rounded bg-muted px-1.5 py-0.5">{item.source}</span>{submitted && <><span aria-hidden="true">·</span><span className="font-medium text-amber-700 dark:text-amber-200">Not independently verified</span>{declaredSource && <><span aria-hidden="true">·</span><span>Declared source: {declaredSource}</span></>}</>}</div></>
  return <div className={`grid grid-cols-[12px_1fr] gap-3 sm:grid-cols-[16px_1fr] sm:gap-4 ${submitted ? 'rounded-lg bg-amber-500/5 px-2 pt-2' : ''}`}><div className="flex flex-col items-center"><span className={`mt-1.5 size-2.5 shrink-0 rounded-full ${item.severity === 'critical' ? 'bg-rose-500' : item.severity === 'warning' ? 'bg-amber-500' : 'bg-foreground'}`} />{!last && <span className="h-full w-px bg-border" />}</div>{item.url ? <a href={item.url} target="_blank" rel="noreferrer" className="min-h-11 pb-5 hover:underline sm:pb-6">{content}</a> : <div className="min-h-11 pb-5 sm:pb-6">{content}</div>}</div>
}

function CrumbCard({ crumb }: { crumb: Crumb }) {
  const { submitted, declaredSource } = crumbSubmissionMetadata(crumb)
  return <div className={`min-w-0 rounded-xl border p-3.5 sm:p-4 ${submitted ? 'border-amber-500/30 bg-amber-500/5' : 'border-border bg-muted/30'}`}><div className="flex flex-wrap items-center gap-2"><Badge tone={crumb.severity === 'critical' ? 'danger' : crumb.severity === 'warning' ? 'warning' : 'default'}>{crumb.severity}</Badge>{submitted && <Badge tone="warning">Submitted by agent</Badge>}<span className="text-xs text-muted-foreground">{crumb.source} · {crumb.category} · {Math.round(crumb.confidence * 100)}%</span></div>{submitted && <div className="mt-2 flex flex-wrap gap-x-3 gap-y-1 text-xs text-amber-800 dark:text-amber-200"><span className="font-semibold">Not independently verified</span>{declaredSource && <span>Declared source: {declaredSource}</span>}</div>}<p className="mt-3 text-sm leading-6 text-foreground">{crumb.summary}</p>{crumb.excerpt && <pre className="mt-3 max-h-40 overflow-auto whitespace-pre-wrap break-words rounded-lg bg-muted p-3 text-xs leading-5 text-muted-foreground">{crumb.excerpt}</pre>}{(crumb.codeReferences?.length ?? 0) > 0 && <div className="mt-3 flex flex-wrap gap-2">{crumb.codeReferences?.map((reference) => <CodeReferenceLink reference={reference} key={reference.id} />)}</div>}{crumb.url && <a className="mt-3 inline-flex min-h-11 items-center text-xs font-medium text-foreground hover:text-foreground" href={crumb.url} target="_blank" rel="noreferrer">Open source ↗</a>}</div>
}

function LogErrorRow({ crumb, first }: { crumb: Crumb; first: boolean }) {
  const content = <div className="flex min-w-0 items-start gap-3"><span className="mt-1.5 size-2 shrink-0 rounded-full bg-amber-500" /><div className="min-w-0 flex-1"><p className="text-sm leading-6 text-foreground">{crumb.summary}</p><p className="mt-1 font-mono text-[11px] text-muted-foreground">{formatTime(crumb.occurredAt)}{crumb.objectId ? ` · ${crumb.objectId}` : ''}</p></div>{first && <Badge tone="warning">first seen</Badge>}</div>
  return crumb.url ? <a className="block p-4 hover:bg-muted/50" href={crumb.url} target="_blank" rel="noreferrer">{content}</a> : <div className="p-4">{content}</div>
}

function normalizeCaseInputId(value: string) {
  return value.replaceAll('-', '').toLowerCase()
}

function declaredSourceForTrail(item: TrailEntry, sources: ReadonlyMap<string, string | null>) {
  const caseInputId = caseInputIdForTrailEntry(item)
  return caseInputId ? sources.get(normalizeCaseInputId(caseInputId)) ?? null : null
}

function fallbackTrailKey(item: TrailEntry) {
  return `${item.occurredAt}-${item.source}-${item.kind}-${item.summary}`
}
