import { AnimatePresence } from 'motion/react'
import type { CausalEvent, Report, Source, SourceHealth, SummaryReference } from '../incidentReport'
import { formatTime } from './formatTime'
import { Badge, CodeReferenceLink, LiveItem } from './ui'

type Diagnoses = NonNullable<NonNullable<Report['ai']>['diagnoses']>

export function SummaryAndCoverage({ report }: { report: Report }) {
  const sources = report.sources ?? []
  return (
    <section className="mb-6 grid gap-4 lg:grid-cols-[1.5fr_1fr]">
      <article className="surface p-4 sm:p-6" aria-labelledby="llm-summary-title">
        <p className="eyebrow">LLM summary</p>
        <h2 id="llm-summary-title" className="sr-only">LLM incident summary</h2>
        <InlineSummary ai={report.ai} fallback={report.deterministicSummary || 'Collectors are preparing the first assessment.'} />
        {report.ai?.status && <p className="mt-4 text-xs text-muted-foreground">AI synthesis: {report.ai.status}. Deterministic evidence remains canonical.</p>}
      </article>
      <article className="surface p-4 sm:p-6">
        <div className="flex items-center justify-between"><p className="eyebrow">Source coverage</p><span className="text-xs text-muted-foreground">{sources.filter((source) => source.health === 'complete').length}/{sources.length}</span></div>
        <div className="mt-4 space-y-3">{sources.length === 0 && <SkeletonRows />}{sources.map((source) => <SourceRow key={source.source} source={source} />)}</div>
      </article>
    </section>
  )
}

export function CausalSequence({ events }: { events: CausalEvent[] }) {
  if (events.length === 0) return null
  return (
    <section id="causal-sequence" className="surface mb-6 scroll-mt-28 p-4 sm:p-6">
      <div className="flex flex-wrap items-end justify-between gap-3"><div><p className="eyebrow">Candidate causal sequence</p><h2 className="mt-2 text-lg font-semibold text-foreground">Change to failure</h2></div><p className="text-xs text-muted-foreground">Chronological evidence, not proof of causation</p></div>
      <div className="relative mt-6 grid gap-3 lg:grid-cols-5"><AnimatePresence initial={false} mode="popLayout">{events.map((event, index) => <LiveItem key={event.id}><CausalEventCard event={event} index={index} /></LiveItem>)}</AnimatePresence></div>
    </section>
  )
}

export function CitedDiagnosis({ diagnoses }: { diagnoses: Diagnoses }) {
  if (diagnoses.length === 0) return null
  return (
    <section id="cited-diagnosis" className="surface mb-6 scroll-mt-28 p-4 sm:p-6">
      <p className="eyebrow">Cited diagnosis</p><h2 className="mt-2 text-lg font-semibold text-foreground">Model assessment tied to collected evidence</h2>
      <div className="relative mt-5 grid gap-3 lg:grid-cols-2"><AnimatePresence initial={false} mode="popLayout">{diagnoses.map((diagnosis) => <LiveItem key={`${diagnosis.summary}-${diagnosis.evidenceIds.join('-')}`}><article className="h-full rounded-xl border border-border bg-muted/30 p-4"><div className="mb-3 flex flex-wrap items-center gap-2">{diagnosis.rank ? <Badge tone={diagnosis.rank === 1 ? 'warning' : 'default'}>Root-cause candidate #{diagnosis.rank}</Badge> : null}{typeof diagnosis.evidenceStrength === 'number' ? <span className="text-xs text-muted-foreground">{diagnosis.evidenceStrength}% evidence strength</span> : null}</div><p className="text-sm leading-6 text-foreground">{diagnosis.summary}</p><div className="mt-3 flex flex-wrap gap-2">{diagnosis.codeReferences.map((reference) => <CodeReferenceLink reference={reference} key={reference.id} />)}{diagnosis.evidenceIds.map((id) => <span className="rounded bg-muted px-2 py-1 font-mono text-[10px] text-muted-foreground" key={id}>evidence {id.slice(0, 8)}</span>)}</div></article></LiveItem>)}</AnimatePresence></div>
    </section>
  )
}

function InlineSummary({ ai, fallback }: { ai: Report['ai']; fallback: string }) {
  const references = new Map<string, SummaryReference>((ai?.summaryReferences ?? []).map((reference) => [reference.id, reference]))
  const parts = ai?.summaryParts?.length ? ai.summaryParts : null
  return <p className="mt-4 text-lg leading-8 text-foreground">{parts ? parts.map((part, index) => { const reference = part.referenceId ? references.get(part.referenceId) : undefined; if (!reference) return <span key={index}>{part.text}</span>; const external = reference.kind === 'external'; return <a className="font-medium underline decoration-foreground/30 underline-offset-4 hover:decoration-foreground" href={reference.href} target={external ? '_blank' : undefined} rel={external ? 'noreferrer' : undefined} title={reference.label} key={`${reference.id}-${index}`}>{part.text}<span className="ml-1 text-xs" aria-hidden="true">{external ? '↗' : '↓'}</span></a> }) : (ai?.summary || fallback)}</p>
}

function SourceRow({ source }: { source: Source }) {
  const colors: Record<SourceHealth, string> = { complete: 'bg-emerald-500', partial: 'bg-amber-500', unavailable: 'bg-rose-500', excluded: 'bg-muted-foreground', pending: 'bg-foreground animate-pulse' }
  return <div className="flex items-center gap-3 text-sm"><span className={`status-dot ${colors[source.health]}`} /><span className="flex-1 capitalize text-foreground">{source.source}</span><span className="font-mono text-xs text-muted-foreground">{source.findingCount ?? 0} · {source.durationMilliseconds ?? 0}ms</span></div>
}

function CausalEventCard({ event, index }: { event: CausalEvent; index: number }) {
  return <article className="relative h-full min-w-0 overflow-hidden rounded-xl border border-border bg-muted/30 p-4"><div className="flex items-center gap-2"><span className="grid size-5 shrink-0 place-items-center rounded-full bg-muted font-mono text-[10px] text-foreground">{index + 1}</span><span className="text-[10px] font-semibold uppercase tracking-wider text-foreground">{event.label ?? event.category}</span></div><p className="mt-3 text-sm leading-6 text-foreground">{event.summary}</p><p className="mt-2 text-xs text-muted-foreground">{formatTime(event.occurredAt)} · {event.source}</p>{event.actor && <p className="mt-1 text-xs text-muted-foreground">Actor: {event.actor}</p>}<div className="mt-3 flex min-w-0 flex-wrap gap-2">{event.codeReferences.map((reference) => <CodeReferenceLink reference={reference} key={reference.id} />)}{event.url && <a className="text-xs font-medium text-foreground" href={event.url} target="_blank" rel="noreferrer">Source ↗</a>}</div></article>
}

function SkeletonRows() {
  return <>{[1, 2, 3].map((item) => <div className="h-4 animate-pulse rounded bg-muted" key={item} />)}</>
}
