import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import type {
  CaseFile,
  CasePending,
  CaseProgressProjection,
  CaseStatus,
  CaseStatusChanged,
  CaseUpdated,
} from '../../api-client/types.gen'
import type {
  CaseFileResponse,
  CaseStatusResponse,
  LiveCaseBackend,
  LiveCaseConnection,
  LiveCaseHandlers,
} from './liveCase'

export function createBrowserLiveCaseBackend(): LiveCaseBackend {
  return {
    async loadCaseFile(caseId, version, signal): Promise<CaseFileResponse> {
      const response = await fetch(`/api/cases/${caseId}`, {
        headers: version ? { 'If-None-Match': `"${version}"` } : {},
        cache: 'no-store',
        signal,
      })
      if (response.status === 200) {
        const body = await response.json() as CaseFile
        return { status: response.status, body }
      }
      if (response.status === 202) {
        const body = await response.json() as CasePending
        return { status: response.status, body }
      }
      return { status: response.status }
    },

    async loadStatus(caseId, signal): Promise<CaseStatusResponse> {
      const response = await fetch(`/api/cases/${caseId}/status`, {
        cache: 'no-store',
        signal,
      })
      const body = response.status === 200
        ? await response.json() as CaseStatus
        : undefined
      return { status: response.status, body }
    },

    createLiveConnection(handlers: LiveCaseHandlers): LiveCaseConnection {
      const connection = new HubConnectionBuilder()
        .withUrl('/hubs/cases')
        .withAutomaticReconnect([0, 1000, 3000, 10000])
        .configureLogging(LogLevel.Warning)
        .build()

      const caseUpdated = (update: CaseUpdated) => handlers.updated(update)
      const caseStatusChanged = (update: CaseStatusChanged) => handlers.statusChanged(update)
      const caseProgressUpdated = (update: CaseProgressProjection) => handlers.progressUpdated(update)

      connection.on('CaseUpdated', caseUpdated)
      connection.on('CaseStatusChanged', caseStatusChanged)
      connection.on('CaseProgressUpdated', caseProgressUpdated)
      connection.onreconnecting(handlers.reconnecting)
      connection.onreconnected(handlers.reconnected)

      return {
        start: () => connection.start(),
        join: (caseId) => connection.invoke('JoinCase', caseId),
        stop: () => connection.stop(),
      }
    },

    repeat(callback, intervalMs) {
      const timer = window.setInterval(callback, intervalMs)
      return () => window.clearInterval(timer)
    },
  }
}
