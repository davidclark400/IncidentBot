import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import type { IncidentPending, IncidentStatus, InvestigationReport } from '../../api-client/types.gen'
import type {
  IncidentStatusResponse,
  InvestigationReportResponse,
  LiveInvestigationConnection,
  LiveInvestigationHandlers,
  LiveInvestigationBackend,
} from './liveInvestigationSession'

export function createBrowserLiveInvestigationBackend(): LiveInvestigationBackend {
  return {
    async loadReport(incidentId, version, signal): Promise<InvestigationReportResponse> {
      const response = await fetch(`/api/incidents/${incidentId}`, {
        headers: version ? { 'If-None-Match': `"${version}"` } : {},
        cache: 'no-store',
        signal,
      })
      const body = response.status === 200 || response.status === 202
        ? await response.json() as IncidentPending | InvestigationReport
        : undefined
      return { status: response.status, body }
    },

    async loadStatus(incidentId, signal): Promise<IncidentStatusResponse> {
      const response = await fetch(`/api/incidents/${incidentId}/status`, {
        cache: 'no-store',
        signal,
      })
      const body = response.status === 200
        ? await response.json() as IncidentStatus
        : undefined
      return { status: response.status, body }
    },

    createLiveConnection(handlers: LiveInvestigationHandlers): LiveInvestigationConnection {
      const connection = new HubConnectionBuilder()
        .withUrl('/hubs/incidents')
        .withAutomaticReconnect([0, 1000, 3000, 10000])
        .configureLogging(LogLevel.Warning)
        .build()

      connection.on('IncidentUpdated', handlers.updated)
      connection.on('IncidentStatusChanged', handlers.statusChanged)
      connection.onreconnecting(handlers.reconnecting)
      connection.onreconnected(handlers.reconnected)

      return {
        start: () => connection.start(),
        join: (incidentId) => connection.invoke('JoinIncident', incidentId),
        stop: () => connection.stop(),
      }
    },

    repeat(callback, intervalMs) {
      const timer = window.setInterval(callback, intervalMs)
      return () => window.clearInterval(timer)
    },
  }
}
