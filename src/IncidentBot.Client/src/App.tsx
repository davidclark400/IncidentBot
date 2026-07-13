import { IncidentPage } from './features/incidents/IncidentPage'
import { RecentIncidentsPage } from './features/recent-incidents/RecentIncidentsPage'
import { useTheme } from './hooks/useTheme'

const incidentId = window.location.pathname.match(/^\/incidents\/([0-9a-f-]+)\/?$/i)?.[1]

function App() {
  const theme = useTheme()

  return incidentId
    ? <IncidentPage incidentId={incidentId} theme={theme} />
    : <RecentIncidentsPage theme={theme} />
}

export default App
