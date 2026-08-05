import type {
  AiSynthesisProgressState,
  CaseEarlyCrumb,
  CaseProgress,
  CaseProgressPhase,
  CaseSourceProgress,
  SourceProgressState,
} from '../../caseProgress'
import { formatTime } from '../../case-file/formatTime'
import { Badge, type BadgeTone } from '../../case-file/ui'
import { formatProgressDuration, sourceDisplayName } from './caseProgressPresentation'

export function CaseProgressPanel({ progress }: { progress: CaseProgress }) {
  const phase = phasePresentation[progress.phase]
  const ai = aiPresentation[progress.aiSynthesisState]
  const coverage = progress.currentPass > 0
    ? `Pass ${progress.currentPass}${progress.currentLookbackMinutes > 0 ? ` · ${progress.currentLookbackMinutes}-minute coverage` : ''}`
    : 'Preparing the first adaptive pass'

  return (
    <section
      className="surface mb-6 overflow-hidden"
      aria-labelledby="case-progress-title"
      data-case-progress
      data-progress-phase={progress.phase}
      data-progress-revision={progress.revision}
    >
      <div className="border-b border-border bg-muted/20 p-4 sm:p-6">
        <div className="flex flex-wrap items-start justify-between gap-4">
          <div>
            <p className="eyebrow">Live case</p>
            <h2 id="case-progress-title" className="mt-2 text-lg font-semibold text-foreground">{phase.title}</h2>
            <p className="mt-1 text-xs text-muted-foreground">
              {coverage} <span className="mx-1 text-border">·</span> Elapsed {formatProgressDuration(progress.elapsedDurationMilliseconds)}
            </p>
          </div>
          <div className="flex flex-wrap gap-2" aria-live="polite">
            <Badge tone={progress.deterministicCaseFileUsable ? 'success' : 'warning'}>
              {progress.deterministicCaseFileUsable ? 'Deterministic Case File usable' : 'Deterministic Case File building'}
            </Badge>
            {progress.onlyAiSynthesisRemaining && <Badge tone="warning">Only AI synthesis remains</Badge>}
            <Badge tone={ai.tone}>AI synthesis {ai.label}</Badge>
          </div>
        </div>
        <p className="mt-3 max-w-3xl text-sm leading-6 text-muted-foreground">{phase.description}</p>
      </div>

      <div className="grid gap-6 p-4 sm:p-6 lg:grid-cols-[1.15fr_0.85fr]">
        <div>
          <div className="flex items-end justify-between gap-3">
            <div>
              <p className="eyebrow">Source progress</p>
              <h3 className="mt-2 text-sm font-semibold text-foreground">Request and health state</h3>
            </div>
            <span className="text-xs text-muted-foreground">{terminalSourceCount(progress.crumbSources)}/{progress.crumbSources.length} finished</span>
          </div>
          <div className="mt-4 grid gap-2" aria-live="polite">
            {progress.crumbSources.length === 0
              ? <p className="rounded-lg border border-dashed border-border px-3 py-5 text-center text-sm text-muted-foreground">No source requests selected.</p>
              : progress.crumbSources.map((source) => <SourceProgressRow source={source} key={source.source} />)}
          </div>
        </div>

        <div>
          <p className="eyebrow">Early top crumbs</p>
          <h3 className="mt-2 text-sm font-semibold text-foreground">Useful before the Case File lands</h3>
          <div className="mt-4 grid gap-2" aria-live="polite">
            {progress.earlyCrumbs.length === 0
              ? <p className="rounded-lg border border-dashed border-border px-3 py-5 text-center text-sm text-muted-foreground">No high-signal crumbs yet.</p>
              : progress.earlyCrumbs.map((crumb) => <EarlyCrumbRow crumb={crumb} key={crumb.id} />)}
          </div>
        </div>
      </div>
    </section>
  )
}

function SourceProgressRow({ source }: { source: CaseSourceProgress }) {
  const state = sourceStatePresentation[source.requestState]
  const scope = [
    source.pass > 0 ? `pass ${source.pass}` : null,
    source.lookbackMinutes > 0 ? `${source.lookbackMinutes}-minute coverage` : null,
  ].filter((part): part is string => part !== null).join(' / ')
  const metadata = [
    healthLabel(source.health),
    scope || null,
    source.durationMilliseconds > 0 ? formatProgressDuration(source.durationMilliseconds) : null,
    source.crumbCount > 0 ? `${source.crumbCount} crumb${source.crumbCount === 1 ? '' : 's'}` : null,
  ].filter((part): part is string => part !== null)

  return (
    <article
      className="rounded-lg border border-border bg-muted/20 px-3 py-3"
      data-source-progress
      data-source={source.source}
      data-source-state={source.requestState}
    >
      <div className="flex items-center gap-3">
        <span className={`size-2 shrink-0 rounded-full ${state.dotClass}`} aria-hidden="true" />
        <span className="min-w-0 flex-1 text-sm font-medium text-foreground">{sourceDisplayName(source.source)}</span>
        <span className={`text-xs font-semibold ${state.textClass}`}>{state.label}</span>
      </div>
      <p className="mt-1.5 pl-5 font-mono text-[10px] text-muted-foreground">{metadata.join(' · ')}</p>
      {source.diagnostic && source.requestState !== 'received' && (
        <p className="mt-1.5 pl-5 text-xs leading-5 text-muted-foreground">{source.diagnostic}</p>
      )}
    </article>
  )
}

function EarlyCrumbRow({ crumb }: { crumb: CaseEarlyCrumb }) {
  return (
    <article className="rounded-lg border border-border bg-muted/20 px-3 py-3" data-early-crumb>
      <div className="flex flex-wrap items-center gap-2">
        <Badge tone={severityTone(crumb.severity)}>{crumb.severity}</Badge>
        <span className="text-xs text-muted-foreground">{sourceDisplayName(crumb.source)} · {Math.round(crumb.confidence * 100)}%</span>
      </div>
      <p className="mt-2 text-sm leading-6 text-foreground">{crumb.summary}</p>
      <p className="mt-1 text-[10px] text-muted-foreground">{formatTime(crumb.occurredAt)}</p>
    </article>
  )
}

function terminalSourceCount(sources: ReadonlyArray<CaseSourceProgress>) {
  return sources.filter((source) => source.requestState === 'received'
    || source.requestState === 'timedOut'
    || source.requestState === 'failed'
    || source.requestState === 'excluded').length
}

function healthLabel(health: CaseSourceProgress['health']) {
  return health.charAt(0).toUpperCase() + health.slice(1)
}

function severityTone(severity: string): BadgeTone {
  if (severity.toLowerCase() === 'critical') return 'danger'
  if (severity.toLowerCase() === 'warning') return 'warning'
  return 'default'
}

const phasePresentation: Record<CaseProgressPhase, { title: string; description: string }> = {
  collecting: {
    title: 'Collecting source crumbs',
    description: 'Adaptive collection is widening only when the deterministic signal remains inconclusive.',
  },
  synthesizing: {
    title: 'Deterministic crumbs are ready',
    description: 'Source collection has finished. The Case File will land after AI synthesis completes.',
  },
  finalizing: {
    title: 'Publishing the Case File',
    description: 'AI synthesis has finished. The completed Case File is being committed as the canonical case result.',
  },
  completed: {
    title: 'Case processing complete',
    description: 'Collection and synthesis have finished; the Case File is ready for responders.',
  },
}

const sourceStatePresentation: Record<SourceProgressState, { label: string; dotClass: string; textClass: string }> = {
  pending: {
    label: 'Pending',
    dotClass: 'bg-muted-foreground/40',
    textClass: 'text-muted-foreground',
  },
  querying: {
    label: 'Querying',
    dotClass: 'animate-pulse bg-amber-500 shadow-sm shadow-amber-500/50',
    textClass: 'text-amber-700 dark:text-amber-300',
  },
  received: {
    label: 'Received',
    dotClass: 'bg-emerald-500 shadow-sm shadow-emerald-500/50',
    textClass: 'text-emerald-700 dark:text-emerald-300',
  },
  timedOut: {
    label: 'Timed out',
    dotClass: 'bg-rose-500 shadow-sm shadow-rose-500/50',
    textClass: 'text-rose-700 dark:text-rose-300',
  },
  failed: {
    label: 'Failed',
    dotClass: 'bg-rose-500 shadow-sm shadow-rose-500/50',
    textClass: 'text-rose-700 dark:text-rose-300',
  },
  excluded: {
    label: 'Excluded',
    dotClass: 'bg-slate-400 dark:bg-slate-500',
    textClass: 'text-slate-600 dark:text-slate-300',
  },
}

const aiPresentation: Record<AiSynthesisProgressState, { label: string; tone: BadgeTone }> = {
  pending: { label: 'pending', tone: 'default' },
  running: { label: 'running', tone: 'warning' },
  complete: { label: 'complete', tone: 'success' },
  unavailable: { label: 'unavailable', tone: 'danger' },
  skipped: { label: 'skipped', tone: 'default' },
}
