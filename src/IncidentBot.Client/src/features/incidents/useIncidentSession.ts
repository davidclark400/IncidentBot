import { useCallback, useEffect, useRef, useState } from 'react'
import { createBrowserLiveInvestigationBackend } from './browserLiveInvestigationBackend'
import {
  createLiveInvestigationSession,
  type LiveInvestigationSession,
  type LiveInvestigationSessionSnapshot,
} from './liveInvestigationSession'

export function useIncidentSession(incidentId: string) {
  const [snapshot, setSnapshot] = useState<LiveInvestigationSessionSnapshot>({
    report: null,
    error: null,
    connected: false,
  })
  const session = useRef<LiveInvestigationSession | null>(null)

  useEffect(() => {
    const current = createLiveInvestigationSession(incidentId, createBrowserLiveInvestigationBackend())
    session.current = current
    const unsubscribe = current.subscribe(setSnapshot)
    current.start()

    return () => {
      unsubscribe()
      if (session.current === current) session.current = null
      void current.dispose()
    }
  }, [incidentId])

  const reload = useCallback(
    () => session.current?.reload() ?? Promise.resolve(),
    [],
  )

  return { ...snapshot, reload }
}
