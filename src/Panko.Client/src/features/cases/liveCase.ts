import type {
  CasePending,
  CaseStatus,
  CaseStatusChanged,
  CaseUpdated,
} from '../../case'
import type { CaseFile } from '../../caseFile'
import type { CaseProgress } from '../../caseProgress'

const pollingIntervalMs = 5000

export type LiveCaseSnapshot = Readonly<{
  caseFile: CaseFile | null
  progress: CaseProgress | null
  error: string | null
  connected: boolean
}>

export type CaseFileResponse = Readonly<{
  status: number
  body?: VersionedCasePending | CaseFile
}>

export type CaseStatusResponse = Readonly<{
  status: number
  body?: VersionedCaseStatus
}>

export type LiveCaseHandlers = Readonly<{
  updated: (update: VersionedCaseUpdated) => void
  statusChanged: (update: VersionedCaseStatusChanged) => void
  progressUpdated: (update: CaseProgress) => void
  reconnecting: () => void
  reconnected: () => void
}>

export interface LiveCaseConnection {
  start(): Promise<void>
  join(caseId: string): Promise<void>
  stop(): Promise<void>
}

/** Internal seam for the owned Case File backend, live transport, and browser clock. */
export interface LiveCaseBackend {
  loadCaseFile(caseId: string, version: number, signal: AbortSignal): Promise<CaseFileResponse>
  loadStatus(caseId: string, signal: AbortSignal): Promise<CaseStatusResponse>
  createLiveConnection(handlers: LiveCaseHandlers): LiveCaseConnection
  repeat(callback: () => void, intervalMs: number): () => void
}

export interface LiveCase {
  getSnapshot(): LiveCaseSnapshot
  subscribe(listener: (snapshot: LiveCaseSnapshot) => void): () => void
  start(): void
  reload(): Promise<void>
  dispose(): Promise<void>
}

export function createLiveCase(
  caseId: string,
  backend: LiveCaseBackend,
): LiveCase {
  return new DefaultLiveCase(caseId, backend)
}

class DefaultLiveCase implements LiveCase {
  private snapshot: LiveCaseSnapshot = {
    caseFile: null,
    progress: null,
    error: null,
    connected: false,
  }
  private readonly listeners = new Set<(snapshot: LiveCaseSnapshot) => void>()
  private caseFileVersion = 0
  private latestStatus: KnownStatus | null = null
  private statusEventSequence = 0
  private latestProgress: CaseProgress | null = null
  private progressEventSequence = 0
  private activeRequest: AbortController | null = null
  private reloadRequested = false
  private reloadTask: Promise<void> | null = null
  private startedReloadCycles = 0
  private catchUpAfterCycle: number | null = null
  private requestedCaseFileVersion = 0
  private active = false
  private disposed = false
  private joined = false
  private connection: LiveCaseConnection | null = null
  private cancelPolling: (() => void) | null = null
  private readonly caseId: string
  private readonly backend: LiveCaseBackend

  constructor(caseId: string, backend: LiveCaseBackend) {
    this.caseId = caseId
    this.backend = backend
  }

  getSnapshot = () => this.snapshot

  subscribe = (listener: (snapshot: LiveCaseSnapshot) => void) => {
    this.listeners.add(listener)
    listener(this.snapshot)
    return () => this.listeners.delete(listener)
  }

  start = () => {
    if (this.active || this.disposed) return
    this.active = true
    this.connection = this.backend.createLiveConnection({
      updated: (update) => {
        this.acceptLiveUpdate(update)
      },
      statusChanged: (update) => {
        this.acceptLiveStatus(update)
      },
      progressUpdated: (update) => {
        this.acceptLiveProgress(update)
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
        // Cleanup is best-effort; the Case is already inactive.
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
      await connection.join(this.caseId)
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
      const caseFileResponse = await this.backend.loadCaseFile(
        this.caseId,
        this.caseFileVersion,
        request.signal,
      )
      if (!this.active) return false
      this.applyCaseFileResponse(caseFileResponse)

      const statusSequenceAtRequest = this.statusEventSequence
      const progressSequenceAtRequest = this.progressEventSequence
      const statusResponse = await this.backend.loadStatus(this.caseId, request.signal)
      if (!this.active) return false
      this.applyStatusResponse(statusResponse, statusSequenceAtRequest, progressSequenceAtRequest)
      this.setSnapshot({ ...this.snapshot, error: null })
      return this.latestStatus === null || this.latestStatus.caseFileVersion <= this.caseFileVersion
    } catch (requestError) {
      if (!this.active) return false
      this.setSnapshot({
        ...this.snapshot,
        error: requestError instanceof Error
          ? requestError.message
          : 'Unable to load this Case.',
      })
      return false
    } finally {
      if (this.activeRequest === request) this.activeRequest = null
    }
  }

  private applyCaseFileResponse(response: CaseFileResponse) {
    if (response.status === 304) return
    if (response.status === 404) {
      throw new Error('This Case does not exist or has expired.')
    }
    if (response.status !== 200 && response.status !== 202) {
      throw new Error(`Case File request failed (${response.status}).`)
    }
    if (!response.body) throw new Error('Unable to load this Case.')
    if (response.body.caseFileVersion < this.caseFileVersion) return

    this.caseFileVersion = response.body.caseFileVersion
    if (this.latestProgress && this.latestProgress.baseCaseFileVersion < this.caseFileVersion) {
      this.latestProgress = null
    }
    if (this.snapshot.progress && this.snapshot.progress.baseCaseFileVersion < this.caseFileVersion) {
      this.setSnapshot({ ...this.snapshot, progress: null })
    }
    if (!this.latestStatus || response.body.caseFileVersion > this.latestStatus.caseFileVersion) {
      this.latestStatus = {
        caseFileVersion: response.body.caseFileVersion,
        status: response.body.status,
        inputVersion: response.body.inputVersion ?? 0,
        projectedInputVersion: response.body.projectedInputVersion ?? response.body.inputVersion ?? 0,
        liveSequence: 0,
      }
    }
    if (this.requestedCaseFileVersion <= this.caseFileVersion) this.requestedCaseFileVersion = 0

    if (response.status === 200) {
      const body = response.body as CaseFile
      const caseFile = this.latestStatus?.caseFileVersion === body.caseFileVersion
        ? applyKnownStatus(body, this.latestStatus)
        : body
      this.setSnapshot({ ...this.snapshot, caseFile })
    }
  }

  private applyStatusResponse(
    response: CaseStatusResponse,
    statusSequenceAtRequest: number,
    progressSequenceAtRequest: number,
  ) {
    if (response.status === 404) {
      throw new Error('This Case does not exist or has expired.')
    }
    if (response.status !== 200) throw new Error(`Case status request failed (${response.status}).`)
    if (!response.body) throw new Error('Unable to load this Case status.')
    if (response.body.caseFileVersion < this.caseFileVersion) return

    const latest = this.latestStatus
    const liveStatusArrivedDuringRequest = latest?.caseFileVersion === response.body.caseFileVersion
      && latest.liveSequence > statusSequenceAtRequest
    if (!latest || response.body.caseFileVersion > latest.caseFileVersion) {
      this.latestStatus = {
        caseFileVersion: response.body.caseFileVersion,
        status: response.body.status,
        inputVersion: response.body.inputVersion ?? 0,
        projectedInputVersion: response.body.projectedInputVersion ?? response.body.inputVersion ?? 0,
        liveSequence: 0,
      }
      this.applyLatestStatusToCaseFile()
    } else if (response.body.caseFileVersion === latest.caseFileVersion) {
      this.latestStatus = {
        ...latest,
        status: liveStatusArrivedDuringRequest ? latest.status : response.body.status,
        inputVersion: Math.max(latest.inputVersion, response.body.inputVersion ?? 0),
        projectedInputVersion: Math.max(latest.projectedInputVersion, response.body.projectedInputVersion ?? 0),
        liveSequence: liveStatusArrivedDuringRequest ? latest.liveSequence : 0,
      }
      this.applyLatestStatusToCaseFile()
    }
    if (this.progressEventSequence <= progressSequenceAtRequest) {
      this.applyAuthoritativeProgress(response.body.progress ?? null)
    }
    if (response.body.caseFileVersion > this.caseFileVersion) {
      this.requireCaseFileCatchUp(response.body.caseFileVersion)
    }
  }

  private acceptLiveStatus(update: VersionedCaseStatusChanged) {
    this.acceptLiveUpdate(update)
  }

  private acceptLiveUpdate(update: VersionedCaseUpdated | VersionedCaseStatusChanged) {
    if (!this.active || update.caseFileVersion < this.caseFileVersion) return
    if (this.latestStatus && update.caseFileVersion < this.latestStatus.caseFileVersion) return

    const previous = this.latestStatus?.caseFileVersion === update.caseFileVersion ? this.latestStatus : null
    const currentCaseFile = this.snapshot.caseFile?.caseFileVersion === update.caseFileVersion ? this.snapshot.caseFile : null
    const status = update.status ?? previous?.status ?? currentCaseFile?.status
    if (!status) {
      if (update.caseFileVersion > this.caseFileVersion) this.requireCaseFileCatchUp(update.caseFileVersion)
      return
    }
    this.latestStatus = {
      caseFileVersion: update.caseFileVersion,
      status,
      inputVersion: Math.max(previous?.inputVersion ?? currentCaseFile?.inputVersion ?? 0, update.inputVersion ?? 0),
      projectedInputVersion: Math.max(previous?.projectedInputVersion ?? currentCaseFile?.projectedInputVersion ?? 0, update.projectedInputVersion ?? 0),
      liveSequence: ++this.statusEventSequence,
    }
    this.applyLatestStatusToCaseFile()
    if (update.caseFileVersion > this.caseFileVersion) this.requireCaseFileCatchUp(update.caseFileVersion)
  }

  private acceptLiveProgress(update: CaseProgress) {
    if (!this.active || !sameCase(update.caseId, this.caseId)) return
    if (!isNewerProgress(update, this.latestProgress, this.caseFileVersion)) return

    this.latestProgress = update
    this.progressEventSequence++
    this.setSnapshot({ ...this.snapshot, progress: update })
    if (update.baseCaseFileVersion > this.caseFileVersion) {
      this.requireCaseFileCatchUp(update.baseCaseFileVersion)
    }
  }

  private applyAuthoritativeProgress(progress: CaseProgress | null) {
    const scopedProgress = progress
      && sameCase(progress.caseId, this.caseId)
      && progress.baseCaseFileVersion >= this.caseFileVersion
      ? progress
      : null
    if (scopedProgress && !isNewerProgress(scopedProgress, this.latestProgress, this.caseFileVersion)) return
    if (scopedProgress) this.latestProgress = scopedProgress
    if (this.snapshot.progress !== scopedProgress) {
      this.setSnapshot({ ...this.snapshot, progress: scopedProgress })
    }
    if (scopedProgress && scopedProgress.baseCaseFileVersion > this.caseFileVersion) {
      this.requireCaseFileCatchUp(scopedProgress.baseCaseFileVersion)
    }
  }

  private applyLatestStatusToCaseFile() {
    const caseFile = this.snapshot.caseFile
    const status = this.latestStatus
    if (!caseFile || !status || caseFile.caseFileVersion !== status.caseFileVersion) return
    const updated = applyKnownStatus(caseFile, status)
    if (updated.status === caseFile.status
      && updated.inputVersion === caseFile.inputVersion
      && updated.projectedInputVersion === caseFile.projectedInputVersion) return
    this.setSnapshot({ ...this.snapshot, caseFile: updated })
  }

  private requireCaseFileCatchUp(version: number) {
    if (!this.active || version <= this.caseFileVersion) return
    if (this.joined) {
      this.catchUpAfterCycle = Math.max(this.catchUpAfterCycle ?? 0, this.startedReloadCycles)
      this.setConnected(false)
    }
    if (version <= this.requestedCaseFileVersion) return
    this.requestedCaseFileVersion = version
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

  private setSnapshot(snapshot: LiveCaseSnapshot) {
    if (snapshot.caseFile === this.snapshot.caseFile
      && snapshot.progress === this.snapshot.progress
      && snapshot.error === this.snapshot.error
      && snapshot.connected === this.snapshot.connected) return
    this.snapshot = snapshot
    this.reconcilePolling()
    for (const listener of this.listeners) listener(snapshot)
  }

  private reconcilePolling() {
    if (!this.active || (this.snapshot.connected && this.snapshot.caseFile)) {
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
  caseFileVersion: number
  status: string
  inputVersion: number
  projectedInputVersion: number
  liveSequence: number
}>

type InputVersions = {
  inputVersion?: number
  projectedInputVersion?: number
}

type VersionedCasePending = CasePending & InputVersions
type VersionedCaseStatus = CaseStatus & InputVersions & {
  progress?: CaseProgress | null
}
type VersionedCaseUpdated = CaseUpdated & InputVersions & { status?: null | string }
type VersionedCaseStatusChanged = CaseStatusChanged & InputVersions

function applyKnownStatus(caseFile: CaseFile, status: KnownStatus): CaseFile {
  return {
    ...caseFile,
    status: status.status,
    inputVersion: Math.max(caseFile.inputVersion ?? 0, status.inputVersion),
    projectedInputVersion: Math.max(caseFile.projectedInputVersion ?? 0, status.projectedInputVersion),
  }
}

function sameCase(left: string, right: string) {
  return left.toLowerCase() === right.toLowerCase()
}

function isNewerProgress(
  candidate: CaseProgress,
  current: CaseProgress | null,
  caseFileVersion: number,
) {
  if (candidate.baseCaseFileVersion < caseFileVersion) return false
  if (!current) return true
  if (candidate.baseCaseFileVersion !== current.baseCaseFileVersion) {
    return candidate.baseCaseFileVersion > current.baseCaseFileVersion
  }
  if (candidate.attemptId === current.attemptId) return candidate.revision > current.revision

  const startedDifference = timestamp(candidate.startedAt) - timestamp(current.startedAt)
  if (startedDifference !== 0) return startedDifference > 0
  return timestamp(candidate.updatedAt) > timestamp(current.updatedAt)
}

function timestamp(value: string) {
  const parsed = Date.parse(value)
  return Number.isNaN(parsed) ? 0 : parsed
}
