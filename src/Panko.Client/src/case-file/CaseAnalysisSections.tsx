import { AnimatePresence } from 'motion/react'
import type { CaseFile, CausalMarker, CrumbSourceHealth, CrumbSourceRequestState, CrumbSourceStatus, SummaryReference } from '../caseFile'
import { formatTime } from './formatTime'
import { Badge, CodeReferenceLink, LiveItem } from './ui'

type Diagnoses = NonNullable<CaseFile['ai']['diagnoses']>

export function SummaryAndCoverage({ caseFile }: { caseFile: CaseFile }) {
  const crumbSources = caseFile.crumbSources ?? []
  const allSourcesComplete = crumbSources.length > 0 && crumbSources.every((source) =>
    source.requestState === 'received' && source.health === 'complete')
  return (
    <section className="mb-6 grid gap-4 lg:grid-cols-[1.5fr_1fr]">
      <article className="surface p-4 sm:p-6" aria-labelledby="llm-summary-title">
        <p className="eyebrow">LLM summary</p>
        <h2 id="llm-summary-title" className="sr-only">LLM case summary</h2>
        <InlineSummary ai={caseFile.ai} fallback={caseFile.deterministicSummary || 'Collectors are preparing the first assessment.'} />
        {caseFile.ai?.status && <p className="mt-4 text-xs text-muted-foreground">AI synthesis: {caseFile.ai.status}. The deterministic Case File remains canonical.</p>}
      </article>
      <article className="surface p-4 sm:p-6">
        <div className="flex items-center justify-between"><p className="eyebrow">Source coverage</p><span className="text-xs text-muted-foreground">{crumbSources.filter((source) => source.health === 'complete').length}/{crumbSources.length}</span></div>
        <div className="mt-4 space-y-3" aria-live="polite">
          {crumbSources.length === 0 && <SkeletonRows />}
          {allSourcesComplete
            ? <AllSourcesComplete />
            : crumbSources.map((source) => <CrumbSourceRow key={source.source} source={source} />)}
        </div>
      </article>
    </section>
  )
}

export function CausalSequence({ markers }: { markers: CausalMarker[] }) {
  if (markers.length === 0) return null
  return (
    <section id="causal-sequence" className="surface mb-6 scroll-mt-28 p-4 sm:p-6">
      <div className="flex flex-wrap items-end justify-between gap-3"><div><p className="eyebrow">Candidate causal sequence</p><h2 className="mt-2 text-lg font-semibold text-foreground">Change to failure</h2></div><p className="text-xs text-muted-foreground">Chronological Crumbs, not proof of causation</p></div>
      <div className="relative mt-6 grid gap-3 lg:grid-cols-5"><AnimatePresence initial={false} mode="popLayout">{markers.map((marker, index) => <LiveItem key={marker.id}><CausalMarkerCard marker={marker} index={index} /></LiveItem>)}</AnimatePresence></div>
    </section>
  )
}

export function CitedDiagnosis({ diagnoses }: { diagnoses: Diagnoses }) {
  if (diagnoses.length === 0) return null
  return (
    <section id="cited-diagnosis" className="surface mb-6 scroll-mt-28 p-4 sm:p-6">
      <p className="eyebrow">Cited diagnosis</p><h2 className="mt-2 text-lg font-semibold text-foreground">Model assessment tied to collected Crumbs</h2>
      <div className="relative mt-5 grid gap-3 lg:grid-cols-2"><AnimatePresence initial={false} mode="popLayout">{diagnoses.map((diagnosis) => <LiveItem key={`${diagnosis.summary}-${diagnosis.crumbIds.join('-')}`}><article className="h-full rounded-xl border border-border bg-muted/30 p-4"><div className="mb-3 flex flex-wrap items-center gap-2">{diagnosis.rank ? <Badge tone={diagnosis.rank === 1 ? 'warning' : 'default'}>Root-cause candidate #{diagnosis.rank}</Badge> : null}{typeof diagnosis.crumbStrength === 'number' ? <span className="text-xs text-muted-foreground">{diagnosis.crumbStrength}% crumb strength</span> : null}</div><p className="text-sm leading-6 text-foreground">{diagnosis.summary}</p><div className="mt-3 flex flex-wrap gap-2">{diagnosis.codeReferences.map((reference) => <CodeReferenceLink reference={reference} key={reference.id} />)}{diagnosis.crumbIds.map((id) => <span className="rounded bg-muted px-2 py-1 font-mono text-[10px] text-muted-foreground" key={id}>crumb {id.slice(0, 8)}</span>)}</div></article></LiveItem>)}</AnimatePresence></div>
    </section>
  )
}

function InlineSummary({ ai, fallback }: { ai: CaseFile['ai']; fallback: string }) {
  const references = new Map<string, SummaryReference>((ai?.summaryReferences ?? []).map((reference) => [reference.id, reference]))
  const parts = ai?.summaryParts?.length ? ai.summaryParts : null
  return <p className="mt-4 text-lg leading-8 text-foreground">{parts ? parts.map((part, index) => { const reference = part.referenceId ? references.get(part.referenceId) : undefined; if (!reference) return <span key={index}>{part.text}</span>; const external = reference.kind === 'external'; return <a className="font-medium underline decoration-foreground/30 underline-offset-4 hover:decoration-foreground" href={reference.href} target={external ? '_blank' : undefined} rel={external ? 'noreferrer' : undefined} title={reference.label} key={`${reference.id}-${index}`}>{part.text}<span className="ml-1 text-xs" aria-hidden="true">{external ? '↗' : '↓'}</span></a> }) : (ai?.summary || fallback)}</p>
}

function CrumbSourceRow({ source }: { source: CrumbSourceStatus }) {
  const state = requestStatePresentation[source.requestState]
  const health = healthPresentation[source.health]
  const metadata = source.requestState === 'requested'
    ? 'Waiting for response'
    : source.requestState === 'errored'
      ? `${source.durationMilliseconds ?? 0}ms`
      : `${health.label} · ${source.crumbCount ?? 0} crumb${source.crumbCount === 1 ? '' : 's'} · ${source.durationMilliseconds ?? 0}ms`

  return (
    <div className="flex items-center gap-3 text-sm" data-source-request data-request-state={source.requestState}>
      <TrafficLight state={source.requestState} />
      <span className="min-w-0 flex-1 capitalize text-foreground">{source.source}</span>
      <span className="text-right">
        <span className={`block text-xs font-semibold ${state.textClass}`}>{state.label}</span>
        <span className="block font-mono text-[10px] text-muted-foreground">{metadata}</span>
      </span>
    </div>
  )
}

function AllSourcesComplete() {
  return (
    <div className="flex items-center gap-3 text-sm text-emerald-700 dark:text-emerald-300">
      <TrafficLight state="received" />
      <span className="font-medium">All source requests received</span>
    </div>
  )
}

function TrafficLight({ state }: { state: CrumbSourceRequestState }) {
  return (
    <span className="flex shrink-0 flex-col gap-0.5 rounded-full border border-border bg-muted/50 p-1" aria-hidden="true">
      <span className={`size-1.5 rounded-full ${state === 'errored' ? 'bg-rose-500 shadow-sm shadow-rose-500/50' : 'bg-rose-500/15'}`} />
      <span className={`size-1.5 rounded-full ${state === 'requested' ? 'animate-pulse bg-amber-500 shadow-sm shadow-amber-500/50' : 'bg-amber-500/15'}`} />
      <span className={`size-1.5 rounded-full ${state === 'received' ? 'bg-emerald-500 shadow-sm shadow-emerald-500/50' : 'bg-emerald-500/15'}`} />
    </span>
  )
}

const requestStatePresentation: Record<CrumbSourceRequestState, { label: string; textClass: string }> = {
  requested: { label: 'Requested', textClass: 'text-amber-700 dark:text-amber-300' },
  received: { label: 'Received', textClass: 'text-emerald-700 dark:text-emerald-300' },
  errored: { label: 'Errored', textClass: 'text-rose-700 dark:text-rose-300' },
}

const healthPresentation: Record<CrumbSourceHealth, { label: string }> = {
  pending: { label: 'pending' },
  complete: { label: 'complete' },
  partial: { label: 'partial' },
  unavailable: { label: 'unavailable' },
  excluded: { label: 'excluded' },
}

function CausalMarkerCard({ marker, index }: { marker: CausalMarker; index: number }) {
  return <article className="relative h-full min-w-0 overflow-hidden rounded-xl border border-border bg-muted/30 p-4"><div className="flex items-center gap-2"><span className="grid size-5 shrink-0 place-items-center rounded-full bg-muted font-mono text-[10px] text-foreground">{index + 1}</span><span className="text-[10px] font-semibold uppercase tracking-wider text-foreground">{marker.label ?? marker.category}</span></div><p className="mt-3 text-sm leading-6 text-foreground">{marker.summary}</p><p className="mt-2 text-xs text-muted-foreground">{formatTime(marker.occurredAt)} · {marker.source}</p>{marker.actor && <p className="mt-1 text-xs text-muted-foreground">Actor: {marker.actor}</p>}<div className="mt-3 flex min-w-0 flex-wrap gap-2">{marker.codeReferences.map((reference) => <CodeReferenceLink reference={reference} key={reference.id} />)}{marker.url && <a className="text-xs font-medium text-foreground" href={marker.url} target="_blank" rel="noreferrer">Source ↗</a>}</div></article>
}

function SkeletonRows() {
  return <>{[1, 2, 3].map((item) => <div className="h-4 animate-pulse rounded bg-muted" key={item} />)}</>
}
