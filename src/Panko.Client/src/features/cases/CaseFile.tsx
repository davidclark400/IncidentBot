import { MotionConfig } from 'motion/react'
import type { ThemeControl } from '../../hooks/useTheme'
import type { CaseFile as CaseFileModel } from '../../caseFile'
import type { CaseProgress as CaseProgressModel } from '../../caseProgress'
import { CausalSequence, CitedDiagnosis, SummaryAndCoverage } from '../../case-file/CaseAnalysisSections'
import { CrumbTrailReview, LogErrorSection } from '../../case-file/CrumbTrailReview'
import { PatternPanel } from '../../case-file/PatternPanel'
import type { CatalogLocation } from '../catalog/catalogModel'
import { CaseProgressPanel } from './CaseProgress'
import { CaseFileHeader } from './CaseFileHeader'
import { CaseInputAudit } from './CaseInputAudit'
import { CaseFileAppHeader } from './CaseFileAppHeader'
import { CaseFileNavigation } from './CaseFileNavigation'
import { ResponderResources } from './ResponderResources'

type CaseFileProps = {
  catalogLocation: CatalogLocation | null
  caseFile: CaseFileModel
  progress: CaseProgressModel | null
  connected: boolean
  warning: string | null
  theme: ThemeControl
  onReplayDemo: () => Promise<void>
}

export function CaseFile({ catalogLocation, caseFile, progress, connected, warning, theme, onReplayDemo }: CaseFileProps) {
  const crumbs = caseFile.crumbs ?? []
  const trail = caseFile.trail ?? []

  return (
    <MotionConfig reducedMotion="user">
      <main className="min-h-screen pb-16 sm:pb-20">
        <CaseFileAppHeader connected={connected} caseFileVersion={caseFile.caseFileVersion} theme={theme} />
        <CaseFileNavigation caseFile={caseFile} />

        <div className="mx-auto max-w-7xl px-4 py-5 sm:px-5 sm:py-8 lg:px-8">
          {warning && <div className="mb-5 rounded-lg border border-amber-500/30 bg-amber-500/10 px-4 py-3 text-sm text-amber-800 dark:text-amber-200">{warning}</div>}
          <CaseFileHeader catalogLocation={catalogLocation} caseFile={caseFile} onReplayDemo={onReplayDemo} />
          {progress && <CaseProgressPanel progress={progress} />}

          {caseFile.pattern && <PatternPanel pattern={caseFile.pattern} />}
          <CrumbTrailReview trail={trail} crumbs={crumbs} />
          <CaseInputAudit
            caseId={caseFile.caseId}
            inputVersion={caseFile.inputVersion ?? 0}
            projectedInputVersion={caseFile.projectedInputVersion ?? caseFile.inputVersion ?? 0}
          />
          <SummaryAndCoverage caseFile={caseFile} />
          <CausalSequence markers={caseFile.causalMarkers ?? []} />
          <CitedDiagnosis diagnoses={caseFile.ai?.diagnoses ?? []} />
          <LogErrorSection crumbs={crumbs} />
          <ResponderResources checks={caseFile.ai?.recommendedChecks ?? []} links={caseFile.links ?? []} />
        </div>
      </main>
    </MotionConfig>
  )
}
