import type { ThemeControl } from '../../hooks/useTheme'
import { resetDemo } from '../demo/demoApi'
import { IncidentErrorPage, IncidentLoadingPage } from './IncidentPageStates'
import { IncidentReport } from './IncidentReport'
import { useIncidentSession } from './useIncidentSession'

export function IncidentPage({ incidentId, theme }: { incidentId: string; theme: ThemeControl }) {
  const { report, error, connected, reload } = useIncidentSession(incidentId)

  if (error && !report) return <IncidentErrorPage message={error} retry={reload} theme={theme} />
  if (!report) return <IncidentLoadingPage theme={theme} />

  const replayDemo = async () => {
    await resetDemo()
    window.location.reload()
  }

  return (
    <IncidentReport
      report={report}
      connected={connected}
      warning={error}
      theme={theme}
      onReplayDemo={replayDemo}
    />
  )
}
