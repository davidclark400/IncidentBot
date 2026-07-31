import type {
  IncidentPending,
  IncidentStatus,
  IncidentStatusChanged,
  IncidentUpdated,
  InvestigationReport,
} from '../../api-client/types.gen'

const pollingIntervalMs = 5000

export type LiveInvestigationSessionSnapshot = Readonly<{
  report: InvestigationReport | null
  error: string | null
  connected: boolean
}>

export type InvestigationReportResponse = Readonly<{
  status: number
  body?: IncidentPending | InvestigationReport
}>

export type IncidentStatusResponse = Readonly<{
  status: number
  body?: IncidentStatus
}>

export type LiveInvestigationHandlers = Readonly<{
  updated: (update: IncidentUpdated) => void
  statusChanged: (update: IncidentStatusChanged) => void
  reconnecting: () => void
  reconnected: () => void
}>

export interface LiveInvestigationConnection {
  start(): Promise<void>
  join(incidentId: string): Promise<void>
  stop(): Promise<void>
}

/** Internal seam for the owned report backend, live transport, and browser clock. */
export interface LiveInvestigationBackend {
  loadReport(incidentId: string, version: number, signal: AbortSignal): Promise<InvestigationReportResponse>
  loadStatus(incidentId: string, signal: AbortSignal): Promise<IncidentStatusResponse>
  createLiveConnection(handlers: LiveInvestigationHandlers): LiveInvestigationConnection
  repeat(callback: () => void, intervalMs: number): () => void
}

export interface LiveInvestigationSession {
  getSnapshot(): LiveInvestigationSessionSnapshot
  subscribe(listener: (snapshot: LiveInvestigationSessionSnapshot) => void): () => void
  start(): void
  reload(): Promise<void>
  dispose(): Promise<void>
}

export function createLiveInvestigationSession(
  incidentId: string,
  backend: LiveInvestigationBackend,
): LiveInvestigationSession {
  return new DefaultLiveInvestigationSession(incidentId, backend)
}

class DefaultLiveInvestigationSession implements LiveInvestigationSession {
  private snapshot: LiveInvestigationSessionSnapshot = {
    report: null,
    error: null,
    connected: false,
  }
  private readonly listeners = new Set<(snapshot: LiveInvestigationSessionSnapshot) => void>()
  private reportVersion = 0
  private latestStatus: KnownStatus | null = null
  private statusEventSequence = 0
  private activeRequest: AbortController | null = null
  private reloadRequested = false
  private reloadTask: Promise<void> | null = null
  private startedReloadCycles = 0
  private catchUpAfterCycle: number | null = null
  private requestedReportVersion = 0
  private active = false
  private disposed = false
  private joined = false
  private connection: LiveInvestigationConnection | null = null
  private cancelPolling: (() => void) | null = null
  private readonly incidentId: string
  private readonly backend: LiveInvestigationBackend

  constructor(incidentId: string, backend: LiveInvestigationBackend) {
    this.incidentId = incidentId
    this.backend = backend
  }

  getSnapshot = () => this.snapshot

  subscribe = (listener: (snapshot: LiveInvestigationSessionSnapshot) => void) => {
    this.listeners.add(listener)
    listener(this.snapshot)
    return () => this.listeners.delete(listener)
  }

  start = () => {
    if (this.active || this.disposed) return
    this.active = true
    this.connection = this.backend.createLiveConnection({
      updated: (update) => {
        if (!this.active || update.version <= this.reportVersion) return
        this.requireReportCatchUp(update.version)
      },
      statusChanged: (update) => {
        this.acceptLiveStatus(update)
      },
      reconnecting: () => this.markDisconnected(),
      reconnected: () => {
        this.markDisconnected()
        void this.joinAndRefresh()
      },
    })
    this.reconcilePolling()
    void this.reload()
    void this.startLiveConnection()
  }

  reload = () => {
    if (!this.active) return Promise.resolve()
    this.reloadRequested = true
    if (!this.reloadTask) this.reloadTask = this.drainReloads()
    return this.reloadTask
  }

  dispose = async () => {
    if (this.disposed) return
    this.active = false
    this.disposed = true
    this.joined = false
    this.catchUpAfterCycle = null
    this.reloadRequested = false
    this.stopPolling()
    this.activeRequest?.abort()
    this.activeRequest = null

    const connection = this.connection
    this.connection = null
    if (this.snapshot.connected) {
      this.setSnapshot({ ...this.snapshot, connected: false })
    }
    if (connection) {
      try {
        await connection.stop()
      } catch {
        // Cleanup is best-effort; the session is already inactive.
      }
    }
    this.listeners.clear()
  }

  private startLiveConnection = async () => {
    const connection = this.connection
    if (!connection) return
    try {
      await connection.start()
    } catch {
      if (this.active && connection === this.connection) this.setConnected(false)
      return
    }
    if (!this.active || connection !== this.connection) return
    await this.joinAndRefresh()
  }

  private joinAndRefresh = async () => {
    const connection = this.connection
    if (!this.active || !connection) return
    try {
      await connection.join(this.incidentId)
    } catch {
      if (this.active && connection === this.connection) this.markDisconnected()
      return
    }
    if (!this.active || connection !== this.connection) return
    this.joined = true
    this.catchUpAfterCycle = this.startedReloadCycles
    this.setConnected(false)
    await this.reload()
  }

  private drainReloads = async () => {
    try {
      while (this.active && this.reloadRequested) {
        this.reloadRequested = false
        const cycle = ++this.startedReloadCycles
        const caughtUp = await this.performReload()
        if (caughtUp
          && this.active
          && this.joined
          && this.catchUpAfterCycle !== null
          && cycle > this.catchUpAfterCycle) {
          this.catchUpAfterCycle = null
          this.setConnected(true)
        }
      }
    } finally {
      this.reloadTask = null
    }
  }

  private performReload = async () => {
    const request = new AbortController()
    this.activeRequest = request
    try {
      const reportResponse = await this.backend.loadReport(
        this.incidentId,
        this.reportVersion,
        request.signal,
      )
      if (!this.active) return false
      this.applyReportResponse(reportResponse)

      const statusSequenceAtRequest = this.statusEventSequence
      const statusResponse = await this.backend.loadStatus(this.incidentId, request.signal)
      if (!this.active) return false
      this.applyStatusResponse(statusResponse, statusSequenceAtRequest)
      this.setSnapshot({ ...this.snapshot, error: null })
      return this.latestStatus === null || this.latestStatus.version <= this.reportVersion
    } catch (requestError) {
      if (!this.active) return false
      this.setSnapshot({
        ...this.snapshot,
        error: requestError instanceof Error
          ? requestError.message
          : 'Unable to load this investigation.',
      })
      return false
    } finally {
      if (this.activeRequest === request) this.activeRequest = null
    }
  }

  private applyReportResponse(response: InvestigationReportResponse) {
    if (response.status === 304) return
    if (response.status === 404) {
      throw new Error('This investigation does not exist or has expired.')
    }
    if (response.status !== 200 && response.status !== 202) {
      throw new Error(`Report request failed (${response.status}).`)
    }
    if (!response.body) throw new Error('Unable to load this investigation.')
    if (response.body.version < this.reportVersion) return

    this.reportVersion = response.body.version
    if (!this.latestStatus || response.body.version > this.latestStatus.version) {
      this.latestStatus = {
        version: response.body.version,
        status: response.body.status,
        liveSequence: 0,
      }
    }
    if (this.requestedReportVersion <= this.reportVersion) this.requestedReportVersion = 0

    if (response.status === 200) {
      const body = response.body as InvestigationReport
      const report = this.latestStatus?.version === body.version && this.latestStatus.status !== body.status
        ? { ...body, status: this.latestStatus.status }
        : body
      this.setSnapshot({ ...this.snapshot, report })
    }
  }

  private applyStatusResponse(response: IncidentStatusResponse, statusSequenceAtRequest: number) {
    if (response.status === 404) {
      throw new Error('This investigation does not exist or has expired.')
    }
    if (response.status !== 200) throw new Error(`Status request failed (${response.status}).`)
    if (!response.body) throw new Error('Unable to load this investigation status.')
    if (response.body.version < this.reportVersion) return

    const latest = this.latestStatus
    const liveStatusArrivedDuringRequest = latest?.version === response.body.version
      && latest.liveSequence > statusSequenceAtRequest
    if (!latest
      || response.body.version > latest.version
      || (response.body.version === latest.version && !liveStatusArrivedDuringRequest)) {
      this.latestStatus = {
        version: response.body.version,
        status: response.body.status,
        liveSequence: 0,
      }
      this.applyLatestStatusToReport()
    }
    if (response.body.version > this.reportVersion) {
      this.requireReportCatchUp(response.body.version)
    }
  }

  private acceptLiveStatus(update: IncidentStatusChanged) {
    if (!this.active || update.version < this.reportVersion) return
    if (this.latestStatus && update.version < this.latestStatus.version) return

    this.latestStatus = {
      version: update.version,
      status: update.status,
      liveSequence: ++this.statusEventSequence,
    }
    this.applyLatestStatusToReport()
    if (update.version > this.reportVersion) this.requireReportCatchUp(update.version)
  }

  private applyLatestStatusToReport() {
    const report = this.snapshot.report
    const status = this.latestStatus
    if (!report || !status || report.version !== status.version || report.status === status.status) return
    this.setSnapshot({ ...this.snapshot, report: { ...report, status: status.status } })
  }

  private requireReportCatchUp(version: number) {
    if (!this.active || version <= this.reportVersion) return
    if (this.joined) {
      this.catchUpAfterCycle = Math.max(this.catchUpAfterCycle ?? 0, this.startedReloadCycles)
      this.setConnected(false)
    }
    if (version <= this.requestedReportVersion) return
    this.requestedReportVersion = version
    void this.reload()
  }

  private markDisconnected() {
    this.joined = false
    this.catchUpAfterCycle = null
    this.setConnected(false)
  }

  private setConnected(connected: boolean) {
    if (!this.active || this.snapshot.connected === connected) return
    this.setSnapshot({ ...this.snapshot, connected })
  }

  private setSnapshot(snapshot: LiveInvestigationSessionSnapshot) {
    if (snapshot.report === this.snapshot.report
      && snapshot.error === this.snapshot.error
      && snapshot.connected === this.snapshot.connected) return
    this.snapshot = snapshot
    this.reconcilePolling()
    for (const listener of this.listeners) listener(snapshot)
  }

  private reconcilePolling() {
    if (!this.active || (this.snapshot.connected && this.snapshot.report)) {
      this.stopPolling()
      return
    }
    if (!this.cancelPolling) {
      this.cancelPolling = this.backend.repeat(() => void this.reload(), pollingIntervalMs)
    }
  }

  private stopPolling() {
    this.cancelPolling?.()
    this.cancelPolling = null
  }
}

type KnownStatus = Readonly<{
  version: number
  status: string
  liveSequence: number
}>
