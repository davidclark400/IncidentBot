import type { CaseFile } from '../caseFile'
import { formatTime } from './formatTime'
import { Badge } from './ui'

type Pattern = NonNullable<CaseFile['pattern']>

export function PatternPanel({ pattern }: { pattern: Pattern }) {
  if (pattern.availability === 'unavailable') {
    return <section className="surface mb-6 border-amber-500/30 p-5"><div className="flex flex-wrap items-center gap-3"><Badge tone="warning">matching unavailable</Badge><p className="text-sm text-muted-foreground">The Case remains available; Pattern context could not be calculated.</p></div></section>
  }
  if (pattern.availability === 'provisional') {
    return <section className="surface mb-6 p-5"><div className="flex flex-wrap items-center gap-3"><Badge>provisional</Badge><p className="text-sm text-muted-foreground">Checking this Case against compact history while Crumb collection completes.</p></div>{pattern.possibleMatches.length > 0 && <RelatedMatches matches={pattern.possibleMatches} />}</section>
  }
  const tone = pattern.lifecycleState === 'resolved' ? 'success' : pattern.lifecycleState === 'regressed' || pattern.lifecycleState === 'escalating' ? 'danger' : pattern.lifecycleState === 'ongoing' ? 'warning' : 'default'
  return (
    <section className="surface mb-6 p-4 sm:p-6" aria-labelledby="pattern-title">
      <div className="flex flex-wrap items-center gap-3"><Badge tone={tone}>{pattern.lifecycleState ?? 'pattern'}</Badge><span className="font-mono text-sm font-semibold tracking-wide text-foreground">{pattern.patternKey}</span>{typeof pattern.matchScore === 'number' && <span className="text-sm text-muted-foreground">{pattern.matchScore}% {pattern.matchType} match</span>}</div>
      <h2 id="pattern-title" className="mt-4 text-lg font-semibold text-foreground">{pattern.occurrenceCount} occurrence{pattern.occurrenceCount === 1 ? '' : 's'}</h2>
      {(pattern.firstSeen || pattern.lastSeen) && <p className="mt-1 text-xs text-muted-foreground">First seen {pattern.firstSeen ? formatTime(pattern.firstSeen) : 'unknown'} · Last seen {pattern.lastSeen ? formatTime(pattern.lastSeen) : 'unknown'}</p>}
      {pattern.matchedFeatures.length > 0 && <p className="mt-4 text-sm text-foreground">Matched on {pattern.matchedFeatures.slice(0, 4).map((item) => item.includes(':') ? item.slice(item.indexOf(':') + 1).trim() : item).join(', ')}.</p>}
      {pattern.recentOccurrences.length > 0 && <div className="mt-5 grid gap-2 sm:grid-cols-2 lg:grid-cols-4">{pattern.recentOccurrences.slice(0, 8).map((occurrence) => { const content = <><span className="font-mono text-xs font-semibold text-foreground">{occurrence.pagerDutyIncidentId}</span><span className="text-xs capitalize text-muted-foreground">{occurrence.pagerDutyState} · {formatTime(occurrence.occurredAt)}</span></>; return occurrence.caseUrl ? <a className="flex flex-col gap-1 rounded-lg border border-border bg-muted/30 p-3 hover:bg-muted/60" href={occurrence.caseUrl} key={occurrence.caseId}>{content}</a> : <div className="flex flex-col gap-1 rounded-lg border border-border bg-muted/30 p-3" key={occurrence.caseId}>{content}</div> })}</div>}
      {pattern.possibleMatches.length > 0 && <RelatedMatches matches={pattern.possibleMatches} />}
    </section>
  )
}

function RelatedMatches({ matches }: { matches: Pattern['possibleMatches'] }) {
  return <div className="mt-5 border-t border-border pt-4"><p className="eyebrow">Possible related cases</p><div className="mt-3 flex flex-wrap gap-2">{matches.slice(0, 5).map((match) => <span className="rounded-md border border-border bg-muted/30 px-3 py-2 text-xs text-muted-foreground" key={`${match.patternKey}-${match.score}`}><span className="font-mono font-semibold text-foreground">{match.patternKey}</span> · {match.score}%</span>)}</div></div>
}
