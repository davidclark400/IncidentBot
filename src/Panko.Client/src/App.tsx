import { CatalogUnavailablePage, OperationsBrowsePage } from './features/catalog/OperationsBrowsePage'
import { parseCatalogRoute } from './features/catalog/catalogModel'
import { CasePage } from './features/cases/CasePage'
import { resolveCaseRoute } from './features/cases/caseRoute'
import { useTheme } from './hooks/useTheme'

function App() {
  const theme = useTheme()
  const caseId = resolveCaseRoute()
  if (caseId) return <CasePage caseId={caseId} theme={theme} />

  const catalogRoute = parseCatalogRoute(window.location.pathname)
  return catalogRoute.kind === 'unavailable'
    ? <CatalogUnavailablePage theme={theme} />
    : <OperationsBrowsePage route={catalogRoute} theme={theme} />
}

export default App
