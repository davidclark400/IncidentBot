import { AnimatePresence } from 'motion/react'
import { useMemo } from 'react'
import type { Finding, TimelineEvent } from '../incidentReport'
import { formatTime } from './formatTime'
import { Badge, CodeReferenceLink, LiveItem } from './ui'

export function TimelineAndEvidence({ timeline, evidence }: { timeline: TimelineEvent[]; evidence: Finding[] }) {
  return (
    <section className="mb-6 grid gap-4 xl:grid-cols-[1fr_1.2fr] xl:gap-6">
      <article id="timeline" className="surface min-w-0 scroll-mt-28 p-4 sm:p-6">
        <div className="flex items-center justify-between">
          <div><p className="eyebrow">Timeline</p><h2 className="mt-2 text-lg font-semibold text-foreground">What changed</h2></div>
          <span className="text-xs text-muted-foreground">UTC</span>
        </div>
        <div className="relative mt-5 space-y-0 sm:mt-6">
          {timeline.length === 0 && <p className="text-sm text-muted-foreground">Waiting for timeline events…</p>}
          <AnimatePresence initial={false} mode="popLayout">
            {timeline.map((item, index) => <LiveItem key={`${item.occurredAt}-${item.source}-${item.kind}-${item.summary}`}><TimelineRow item={item} last={index === timeline.length - 1} /></LiveItem>)}
          </AnimatePresence>
        </div>
      </article>

      <article id="evidence" className="surface min-w-0 scroll-mt-28 p-4 sm:p-6">
        <div className="flex items-center justify-between gap-3">
          <div><p className="eyebrow">Evidence</p><h2 className="mt-2 text-lg font-semibold text-foreground">Highest-signal findings</h2></div>
          <span className="shrink-0 text-xs text-muted-foreground">
            {evidence.length > 25 ? `Top 25 of ${evidence.length} findings` : `${evidence.length} findings`}
          </span>
        </div>
        <div className="relative mt-5 space-y-3">
          {evidence.length === 0 && <p className="text-sm text-muted-foreground">Collectors are querying the allowlisted sources…</p>}
          <AnimatePresence initial={false} mode="popLayout">
            {evidence.slice(0, 25).map((finding) => <LiveItem key={finding.id}><EvidenceCard finding={finding} /></LiveItem>)}
          </AnimatePresence>
        </div>
      </article>
    </section>
  )
}

export function LogErrorSection({ evidence }: { evidence: Finding[] }) {
  const errors = useMemo(
    () => evidence
      .filter((finding) => finding.source.toLowerCase() === 'victorialogs' && ['first-error', 'log-sample'].includes(finding.category))
      .sort((a, b) => new Date(a.occurredAt).getTime() - new Date(b.occurredAt).getTime()),
    [evidence],
  )
  if (errors.length === 0) return null
  return (
    <section id="log-errors" className="surface mb-6 scroll-mt-28 p-4 sm:p-6">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div><p className="eyebrow">Summarised log error lines</p><h2 className="mt-2 text-lg font-semibold text-foreground">Errors around the incident window</h2></div>
        <span className="text-xs text-muted-foreground">{errors.length} sampled line{errors.length === 1 ? '' : 's'} · UTC</span>
      </div>
      <div className="relative mt-5 divide-y divide-border rounded-xl border border-border bg-muted/30">
        <AnimatePresence initial={false} mode="popLayout">
          {errors.slice(0, 20).map((finding, index) => <LiveItem key={finding.id}><LogErrorRow finding={finding} first={index === 0} /></LiveItem>)}
        </AnimatePresence>
      </div>
    </section>
  )
}

function TimelineRow({ item, last }: { item: TimelineEvent; last: boolean }) {
  const content = <><p className="text-sm leading-6 text-foreground">{item.summary}</p><div className="mt-1.5 flex flex-wrap items-center gap-x-2 gap-y-1 text-xs text-muted-foreground"><time dateTime={item.occurredAt}>{formatTime(item.occurredAt)}</time><span aria-hidden="true">·</span><span className="rounded bg-muted px-1.5 py-0.5">{item.source}</span></div></>
  return <div className="grid grid-cols-[12px_1fr] gap-3 sm:grid-cols-[16px_1fr] sm:gap-4"><div className="flex flex-col items-center"><span className={`mt-1.5 size-2.5 shrink-0 rounded-full ${item.severity === 'critical' ? 'bg-rose-500' : item.severity === 'warning' ? 'bg-amber-500' : 'bg-foreground'}`} />{!last && <span className="h-full w-px bg-border" />}</div>{item.url ? <a href={item.url} target="_blank" rel="noreferrer" className="min-h-11 pb-5 hover:underline sm:pb-6">{content}</a> : <div className="min-h-11 pb-5 sm:pb-6">{content}</div>}</div>
}

function EvidenceCard({ finding }: { finding: Finding }) {
  return <div className="min-w-0 rounded-xl border border-border bg-muted/30 p-3.5 sm:p-4"><div className="flex flex-wrap items-center gap-2"><Badge tone={finding.severity === 'critical' ? 'danger' : finding.severity === 'warning' ? 'warning' : 'default'}>{finding.severity}</Badge><span className="text-xs text-muted-foreground">{finding.source} · {finding.category} · {Math.round(finding.confidence * 100)}%</span></div><p className="mt-3 text-sm leading-6 text-foreground">{finding.summary}</p>{finding.excerpt && <pre className="mt-3 max-h-40 overflow-auto whitespace-pre-wrap break-words rounded-lg bg-muted p-3 text-xs leading-5 text-muted-foreground">{finding.excerpt}</pre>}{(finding.codeReferences?.length ?? 0) > 0 && <div className="mt-3 flex flex-wrap gap-2">{finding.codeReferences?.map((reference) => <CodeReferenceLink reference={reference} key={reference.id} />)}</div>}{finding.url && <a className="mt-3 inline-flex min-h-11 items-center text-xs font-medium text-foreground hover:text-foreground" href={finding.url} target="_blank" rel="noreferrer">Open source ↗</a>}</div>
}

function LogErrorRow({ finding, first }: { finding: Finding; first: boolean }) {
  const content = <div className="flex min-w-0 items-start gap-3"><span className="mt-1.5 size-2 shrink-0 rounded-full bg-amber-500" /><div className="min-w-0 flex-1"><p className="text-sm leading-6 text-foreground">{finding.summary}</p><p className="mt-1 font-mono text-[11px] text-muted-foreground">{formatTime(finding.occurredAt)}{finding.objectId ? ` · ${finding.objectId}` : ''}</p></div>{first && <Badge tone="warning">first seen</Badge>}</div>
  return finding.url ? <a className="block p-4 hover:bg-muted/50" href={finding.url} target="_blank" rel="noreferrer">{content}</a> : <div className="p-4">{content}</div>
}
