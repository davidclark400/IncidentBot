import type { CaseFile } from '../../caseFile'
import { formatTime } from '../../case-file/formatTime'
import { Badge } from '../../case-file/ui'
import { CatalogBreadcrumbs } from '../catalog/CatalogBreadcrumbs'
import type { CatalogLocation } from '../catalog/catalogModel'

export function CaseFileHeader({ catalogLocation, caseFile, onReplayDemo }: { catalogLocation: CatalogLocation | null; caseFile: CaseFile; onReplayDemo: () => Promise<void> }) {
  const isResolved = caseFile.pagerDutyState.toLowerCase() === 'resolved'
  const origin = caseFile.origin?.kind ?? (caseFile.pagerDutyIncidentId ? 'pagerDuty' : 'manual')
  const inputVersion = caseFile.inputVersion ?? 0
  const projectedInputVersion = caseFile.projectedInputVersion ?? inputVersion
  const isProjectionLagging = inputVersion > projectedInputVersion

  return (
    <header className="mb-5 grid gap-4 sm:mb-8 sm:gap-6 lg:grid-cols-[1fr_auto] lg:items-end">
      <div className="min-w-0 lg:col-span-2">
        {catalogLocation
          ? <CatalogBreadcrumbs {...catalogLocation} current="case" />
          : <nav aria-label="Breadcrumb" className="mb-5 text-xs"><a href="/" className="font-medium text-muted-foreground hover:text-foreground">All operations</a><span aria-hidden="true" className="mx-2 text-border">/</span><span aria-current="page" className="font-semibold text-foreground">Case</span></nav>}
      </div>
      <div>
        <div className="mb-4 flex flex-wrap items-center gap-2">
          <Badge tone={isResolved ? 'success' : caseFile.urgency === 'high' ? 'danger' : 'warning'}>{caseFile.pagerDutyState}</Badge>
          <Badge>{caseFile.urgency} urgency</Badge>
          <Badge>{originLabel(origin)}</Badge>
          {caseFile.pagerDutyIncidentId && <span className="font-mono text-xs text-muted-foreground">{caseFile.pagerDutyIncidentId}</span>}
        </div>
        <h1 className="max-w-4xl text-2xl font-semibold tracking-tight text-foreground sm:text-4xl">{caseFile.title}</h1>
        <p className="mt-3 text-sm text-muted-foreground">
          {caseFile.serviceId} <span className="mx-2 text-border">/</span> Started {formatTime(caseFile.openedAt)}
        </p>
      </div>
      <div className="text-left text-xs text-muted-foreground lg:text-right">
        <p>Recipe {caseFile.recipeId}</p>
        <p className="mt-1">Revision {caseFile.recipeRevision ?? 'loading'}</p>
        <p className="mt-1">Inputs {projectedInputVersion} of {inputVersion} projected</p>
        {caseFile.createdBy && <p className="mt-1">Created by {caseFile.createdBy}</p>}
        {caseFile.pagerDutyIncidentId === 'PDEMO' && <button className="mt-3 rounded-md border border-border px-3 py-1.5 font-semibold text-foreground hover:bg-muted/50" onClick={() => void onReplayDemo()}>Replay demo</button>}
      </div>
      {isProjectionLagging && (
        <div role="status" aria-live="polite" className="rounded-lg border border-amber-500/30 bg-amber-500/10 px-4 py-3 text-sm text-amber-800 dark:text-amber-200 lg:col-span-2">
          <span className="font-semibold">Rebuilding from new inputs</span>
          <span className="ml-2 text-xs">Input v{inputVersion} · projected v{projectedInputVersion}</span>
        </div>
      )}
    </header>
  )
}

function originLabel(origin: string) {
  if (origin.toLowerCase() === 'pagerduty') return 'PagerDuty'
  if (origin.toLowerCase() === 'agent') return 'Agent-created'
  return 'Manual'
}
