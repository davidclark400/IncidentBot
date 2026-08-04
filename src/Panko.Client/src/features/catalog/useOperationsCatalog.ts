import { useCallback, useEffect, useState } from 'react'
import { getOperationsCatalog } from './catalogApi'
import type { OperationsCatalog } from './catalogModel'

export function useOperationsCatalog() {
  const [catalog, setCatalog] = useState<OperationsCatalog | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [refreshSequence, setRefreshSequence] = useState(0)

  useEffect(() => {
    const controller = new AbortController()
    setLoading(true)
    setError(null)
    void getOperationsCatalog(controller.signal)
      .then((result) => {
        setCatalog(result)
        setLoading(false)
      })
      .catch((reason: unknown) => {
        if (controller.signal.aborted) return
        setError(reason instanceof Error ? reason.message : 'The operations catalog could not be loaded.')
        setLoading(false)
      })
    return () => controller.abort()
  }, [refreshSequence])

  return {
    catalog,
    error,
    loading,
    refresh: useCallback(() => setRefreshSequence((value) => value + 1), []),
  }
}
