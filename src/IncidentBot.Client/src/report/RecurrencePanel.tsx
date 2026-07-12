import type { Report } from '../incidentReport'
import { formatTime } from './formatTime'
import { Badge } from './ui'

type Problem = NonNullable<Report['problem']>

export function RecurrencePanel({ problem }: { problem: Problem }) {
  if (problem.availability === 'unavailable') {
    return <section className="surface mb-6 border-amber-500/30 p-5"><div className="flex flex-wrap items-center gap-3"><Badge tone="warning">matching unavailable</Badge><p className="text-sm text-muted-foreground">The investigation remains available; recurrence context could not be calculated.</p></div></section>
  }
  if (problem.availability === 'provisional') {
    return <section className="surface mb-6 p-5"><div className="flex flex-wrap items-center gap-3"><Badge>provisional</Badge><p className="text-sm text-muted-foreground">Checking this incident against compact history while evidence collection completes.</p></div>{problem.possibleMatches.length > 0 && <RelatedMatches matches={problem.possibleMatches} />}</section>
  }
  const tone = problem.lifecycleState === 'resolved' ? 'success' : problem.lifecycleState === 'regressed' || problem.lifecycleState === 'escalating' ? 'danger' : problem.lifecycleState === 'ongoing' ? 'warning' : 'default'
  return (
    <section className="surface mb-6 p-4 sm:p-6" aria-labelledby="problem-title">
      <div className="flex flex-wrap items-center gap-3"><Badge tone={tone}>{problem.lifecycleState ?? 'problem'}</Badge><span className="font-mono text-sm font-semibold tracking-wide text-foreground">{problem.problemKey}</span>{typeof problem.matchScore === 'number' && <span className="text-sm text-muted-foreground">{problem.matchScore}% {problem.matchType} match</span>}</div>
      <h2 id="problem-title" className="mt-4 text-lg font-semibold text-foreground">{problem.occurrenceCount} occurrence{problem.occurrenceCount === 1 ? '' : 's'}</h2>
      {(problem.firstSeen || problem.lastSeen) && <p className="mt-1 text-xs text-muted-foreground">First seen {problem.firstSeen ? formatTime(problem.firstSeen) : 'unknown'} · Last seen {problem.lastSeen ? formatTime(problem.lastSeen) : 'unknown'}</p>}
      {problem.matchedFeatures.length > 0 && <p className="mt-4 text-sm text-foreground">Matched on {problem.matchedFeatures.slice(0, 4).map((item) => item.includes(':') ? item.slice(item.indexOf(':') + 1).trim() : item).join(', ')}.</p>}
      {problem.recentOccurrences.length > 0 && <div className="mt-5 grid gap-2 sm:grid-cols-2 lg:grid-cols-4">{problem.recentOccurrences.slice(0, 8).map((occurrence) => { const content = <><span className="font-mono text-xs font-semibold text-foreground">{occurrence.pagerDutyIncidentId}</span><span className="text-xs capitalize text-muted-foreground">{occurrence.state} · {formatTime(occurrence.occurredAt)}</span></>; return occurrence.reportUrl ? <a className="flex flex-col gap-1 rounded-lg border border-border bg-muted/30 p-3 hover:bg-muted/60" href={occurrence.reportUrl} key={occurrence.incidentId}>{content}</a> : <div className="flex flex-col gap-1 rounded-lg border border-border bg-muted/30 p-3" key={occurrence.incidentId}>{content}</div> })}</div>}
      {problem.possibleMatches.length > 0 && <RelatedMatches matches={problem.possibleMatches} />}
    </section>
  )
}

function RelatedMatches({ matches }: { matches: Problem['possibleMatches'] }) {
  return <div className="mt-5 border-t border-border pt-4"><p className="eyebrow">Possible related incidents</p><div className="mt-3 flex flex-wrap gap-2">{matches.slice(0, 5).map((match) => <span className="rounded-md border border-border bg-muted/30 px-3 py-2 text-xs text-muted-foreground" key={`${match.problemKey}-${match.score}`}><span className="font-mono font-semibold text-foreground">{match.problemKey}</span> · {match.score}%</span>)}</div></div>
}
