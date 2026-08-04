import type { ThemeControl } from '../../hooks/useTheme'
import { resetDemo } from '../demo/demoApi'
import { findCatalogLocation } from '../catalog/catalogModel'
import { useOperationsCatalog } from '../catalog/useOperationsCatalog'
import { CaseErrorPage, CaseLoadingPage } from './CasePageStates'
import { CaseFile } from './CaseFile'
import { useCase } from './useCase'

export function CasePage({ caseId, theme }: { caseId: string; theme: ThemeControl }) {
  const { caseFile, progress, error, connected, reload } = useCase(caseId)
  const { catalog } = useOperationsCatalog()

  if (error && !caseFile) return <CaseErrorPage message={error} retry={reload} theme={theme} />
  if (!caseFile) return <CaseLoadingPage theme={theme} />

  const replayDemo = async () => {
    await resetDemo()
    window.location.reload()
  }

  return (
    <CaseFile
      catalogLocation={catalog ? findCatalogLocation(catalog, caseFile.recipeId, caseFile.serviceId) : null}
      caseFile={caseFile}
      progress={progress}
      connected={connected}
      warning={error}
      theme={theme}
      onReplayDemo={replayDemo}
    />
  )
}
