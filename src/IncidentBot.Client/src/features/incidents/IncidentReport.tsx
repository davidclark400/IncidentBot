import { MotionConfig } from 'motion/react'
import type { ThemeControl } from '../../hooks/useTheme'
import type { Report } from '../../incidentReport'
import { CausalSequence, CitedDiagnosis, SummaryAndCoverage } from '../../report/AnalysisSections'
import { LogErrorSection, TimelineAndEvidence } from '../../report/EvidenceReview'
import { RecurrencePanel } from '../../report/RecurrencePanel'
import { IncidentReportHeader } from './IncidentReportHeader'
import { ReportAppHeader } from './ReportAppHeader'
import { ReportNavigation } from './ReportNavigation'
import { ResponderResources } from './ResponderResources'

type IncidentReportProps = {
  report: Report
  connected: boolean
  warning: string | null
  theme: ThemeControl
  onReplayDemo: () => Promise<void>
}

export function IncidentReport({ report, connected, warning, theme, onReplayDemo }: IncidentReportProps) {
  const evidence = report.evidence ?? []
  const timeline = report.timeline ?? []

  return (
    <MotionConfig reducedMotion="user">
      <main className="min-h-screen pb-16 sm:pb-20">
        <ReportAppHeader connected={connected} reportVersion={report.version} theme={theme} />
        <ReportNavigation report={report} />

        <div className="mx-auto max-w-7xl px-4 py-5 sm:px-5 sm:py-8 lg:px-8">
          {warning && <div className="mb-5 rounded-lg border border-amber-500/30 bg-amber-500/10 px-4 py-3 text-sm text-amber-800 dark:text-amber-200">{warning}</div>}
          <IncidentReportHeader report={report} onReplayDemo={onReplayDemo} />

          {report.problem && <RecurrencePanel problem={report.problem} />}
          <TimelineAndEvidence timeline={timeline} evidence={evidence} />
          <SummaryAndCoverage report={report} />
          <CausalSequence events={report.causalEvents ?? []} />
          <CitedDiagnosis diagnoses={report.ai?.diagnoses ?? []} />
          <LogErrorSection evidence={evidence} />
          <ResponderResources checks={report.ai?.recommendedChecks ?? []} links={report.links ?? []} />
        </div>
      </main>
    </MotionConfig>
  )
}
