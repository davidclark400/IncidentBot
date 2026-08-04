import { useCallback, useEffect, useRef, useState } from 'react'
import { createBrowserLiveCaseBackend } from './browserLiveCaseBackend'
import {
  createLiveCase,
  type LiveCase,
  type LiveCaseSnapshot,
} from './liveCase'

export function useCase(caseId: string) {
  const [snapshot, setSnapshot] = useState<LiveCaseSnapshot>({
    caseFile: null,
    progress: null,
    error: null,
    connected: false,
  })
  const liveCase = useRef<LiveCase | null>(null)

  useEffect(() => {
    const current = createLiveCase(caseId, createBrowserLiveCaseBackend())
    liveCase.current = current
    const unsubscribe = current.subscribe(setSnapshot)
    current.start()

    return () => {
      unsubscribe()
      if (liveCase.current === current) liveCase.current = null
      void current.dispose()
    }
  }, [caseId])

  const reload = useCallback(
    () => liveCase.current?.reload() ?? Promise.resolve(),
    [],
  )

  return { ...snapshot, reload }
}
