import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { useCallback, useEffect, useRef, useState } from 'react'
import type { IncidentPending, IncidentStatusChanged, IncidentUpdated, InvestigationReport } from '../../api-client/types.gen'

export function useIncidentSession(incidentId: string) {
  const [report, setReport] = useState<InvestigationReport | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [connected, setConnected] = useState(false)
  const version = useRef(0)

  const loadReport = useCallback(async () => {
    try {
      const response = await fetch(`/api/incidents/${incidentId}`, {
        headers: version.current ? { 'If-None-Match': `"${version.current}"` } : {},
        cache: 'no-store',
      })
      if (response.status === 304) return
      if (response.status === 404) throw new Error('This investigation does not exist or has expired.')
      if (!response.ok && response.status !== 202) throw new Error(`Report request failed (${response.status}).`)
      const next = (await response.json()) as IncidentPending | InvestigationReport
      if (next.version < version.current) return
      version.current = next.version
      if (response.status === 200) setReport(next as InvestigationReport)
      setError(null)
    } catch (requestError) {
      setError(requestError instanceof Error ? requestError.message : 'Unable to load this investigation.')
    }
  }, [incidentId])

  useEffect(() => {
    void loadReport()

    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/incidents')
      .withAutomaticReconnect([0, 1000, 3000, 10000])
      .configureLogging(LogLevel.Warning)
      .build()
    const joinAndRefresh = async () => {
      setConnected(true)
      await connection.invoke('JoinIncident', incidentId)
      await loadReport()
    }

    connection.on('IncidentUpdated', (update: IncidentUpdated) => {
      if (update.version > version.current) void loadReport()
    })
    connection.on('IncidentStatusChanged', (_update: IncidentStatusChanged) => void loadReport())
    connection.onreconnecting(() => setConnected(false))
    connection.onreconnected(joinAndRefresh)
    connection.start().then(joinAndRefresh).catch(() => setConnected(false))

    return () => {
      void connection.stop()
    }
  }, [incidentId, loadReport])

  useEffect(() => {
    if (connected && report) return
    const timer = window.setInterval(() => void loadReport(), 5000)
    return () => window.clearInterval(timer)
  }, [connected, loadReport, report])

  return { report, error, connected, reload: loadReport }
}
