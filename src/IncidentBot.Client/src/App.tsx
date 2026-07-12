import { DemoLandingPage } from './features/demo/DemoLandingPage'
import { IncidentPage } from './features/incidents/IncidentPage'
import { useTheme } from './hooks/useTheme'

const incidentId = window.location.pathname.match(/^\/incidents\/([0-9a-f-]+)\/?$/i)?.[1]

function App() {
  const theme = useTheme()

  return incidentId
    ? <IncidentPage incidentId={incidentId} theme={theme} />
    : <DemoLandingPage theme={theme} />
}

export default App
