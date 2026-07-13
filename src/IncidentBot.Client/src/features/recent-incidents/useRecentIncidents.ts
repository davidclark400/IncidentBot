import { useCallback, useEffect, useState } from 'react'
import type { RecentPagerDutyIncidents } from '../../api-client/types.gen'
import { getRecentPagerDutyIncidents, triggerPagerDutyIncident } from './recentIncidentsApi'

export function useRecentIncidents() {
  const [hours, setHours] = useState(24)
  const [refreshSequence, setRefreshSequence] = useState(0)
  const [data, setData] = useState<RecentPagerDutyIncidents | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [triggeringId, setTriggeringId] = useState<string | null>(null)
  const [triggerError, setTriggerError] = useState<{ id: string; message: string } | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    setLoading(true)
    setError(null)
    void getRecentPagerDutyIncidents(hours, controller.signal)
      .then((result) => {
        setData(result)
        setLoading(false)
      })
      .catch((reason: unknown) => {
        if (controller.signal.aborted) return
        setError(reason instanceof Error ? reason.message : 'PagerDuty incidents could not be loaded.')
        setLoading(false)
      })
    return () => controller.abort()
  }, [hours, refreshSequence])

  const refresh = useCallback(() => setRefreshSequence((value) => value + 1), [])

  const startInvestigation = useCallback(async (id: string) => {
    setTriggeringId(id)
    setTriggerError(null)
    try {
      const result = await triggerPagerDutyIncident(id)
      window.location.assign(result.incidentUrl)
    } catch (reason) {
      setTriggerError({
        id,
        message: reason instanceof Error ? reason.message : 'The investigation could not be started.',
      })
      setTriggeringId(null)
    }
  }, [])

  return {
    data,
    error,
    hours,
    loading,
    refresh,
    setHours,
    startInvestigation,
    triggerError,
    triggeringId,
  }
}
