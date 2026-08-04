import { useCallback, useEffect, useMemo, useState } from 'react'
import type { RecentPagerDutyIncidents } from '../../api-client/types.gen'
import type { RecentCases } from '../../case'
import type { CatalogScope } from '../catalog/catalogModel'
import { scopeMatchesPagerDutyService, scopeMatchesRecipe } from '../catalog/catalogModel'
import { getRecentCases, getRecentPagerDutyIncidents, openPagerDutyCase } from './caseActivityApi'

const supportedHours = new Set([6, 24, 72, 168, 720])

export function useCaseActivity(scope: CatalogScope) {
  const [hours, setHoursState] = useState(readHoursFromLocation)
  const [refreshSequence, setRefreshSequence] = useState(0)
  const [data, setData] = useState<RecentPagerDutyIncidents | null>(null)
  const [cases, setCases] = useState<RecentCases | null>(null)
  const [loading, setLoading] = useState(true)
  const [casesLoading, setCasesLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [casesError, setCasesError] = useState<string | null>(null)
  const [openingId, setOpeningId] = useState<string | null>(null)
  const [openError, setOpenError] = useState<{ id: string; message: string } | null>(null)

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

  useEffect(() => {
    const controller = new AbortController()
    setCasesLoading(true)
    setCasesError(null)
    void getRecentCases(controller.signal)
      .then((result) => {
        setCases(result)
        setCasesLoading(false)
      })
      .catch((reason: unknown) => {
        if (controller.signal.aborted) return
        setCasesError(reason instanceof Error ? reason.message : 'Recent Cases could not be loaded.')
        setCasesLoading(false)
      })
    return () => controller.abort()
  }, [refreshSequence])

  const refresh = useCallback(() => setRefreshSequence((value) => value + 1), [])

  const setHours = useCallback((value: number) => {
    const normalized = supportedHours.has(value) ? value : 24
    setHoursState(normalized)
    const url = new URL(window.location.href)
    url.searchParams.set('hours', String(normalized))
    window.history.replaceState(null, '', `${url.pathname}${url.search}${url.hash}`)
  }, [])

  const openCase = useCallback(async (id: string) => {
    setOpeningId(id)
    setOpenError(null)
    try {
      const result = await openPagerDutyCase(id)
      window.location.assign(result.caseUrl)
    } catch (reason) {
      setOpenError({
        id,
        message: reason instanceof Error ? reason.message : 'The Case could not be opened.',
      })
      setOpeningId(null)
    }
  }, [])

  const scopedData = useMemo<RecentPagerDutyIncidents | null>(() => data && ({
    ...data,
    incidents: data.incidents.filter((incident) => scopeMatchesPagerDutyService(scope, incident.serviceId)),
  }), [data, scope])
  const scopedCases = useMemo<RecentCases | null>(() => {
    if (!cases) return null
    const visible = cases.cases.filter((caseItem) => scopeMatchesRecipe(scope, caseItem.recipeId))
    return { total: visible.length, cases: visible }
  }, [scope, cases])

  return {
    data: scopedData,
    error,
    hours,
    loading,
    refresh,
    cases: scopedCases,
    casesError,
    casesLoading,
    setHours,
    openCase,
    openError,
    openingId,
  }
}

export function parseRecentHours(search: string) {
  const value = Number(new URLSearchParams(search).get('hours'))
  return supportedHours.has(value) ? value : 24
}

function readHoursFromLocation() {
  return parseRecentHours(window.location.search)
}
