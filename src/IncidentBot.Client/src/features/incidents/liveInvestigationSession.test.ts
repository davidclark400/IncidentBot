import { describe, expect, it } from 'vitest'
import type {
  IncidentPending,
  IncidentStatus,
  IncidentStatusChanged,
  IncidentUpdated,
  InvestigationReport,
} from '../../api-client/types.gen'
import {
  createLiveInvestigationSession,
  type IncidentStatusResponse,
  type InvestigationReportResponse,
  type LiveInvestigationConnection,
  type LiveInvestigationHandlers,
  type LiveInvestigationBackend,
} from './liveInvestigationSession'

describe('Live investigation session', () => {
  it('serializes and coalesces reloads while a read is in flight', async () => {
    const backend = new FakeBackend()
    backend.connection.startError = new Error('offline')
    const first = deferred<InvestigationReportResponse>()
    backend.enqueue(first.promise, reportResponse(2))
    backend.enqueueStatuses(statusResponse(1), statusResponse(2))
    const session = createLiveInvestigationSession('incident-1', backend)

    session.start()
    await settle()
    const firstReload = session.reload()
    const secondReload = session.reload()

    expect(backend.requestedVersions).toEqual([0])
    expect(backend.maxConcurrentReportReads).toBe(1)

    first.resolve(reportResponse(1))
    await Promise.all([firstReload, secondReload])

    expect(session.getSnapshot().report?.version).toBe(2)
    expect(backend.requestedVersions).toEqual([0, 1])
    expect(backend.maxConcurrentReportReads).toBe(1)
    await session.dispose()
  })

  it('retains a live status when an in-flight same-version report has an older status', async () => {
    const backend = new FakeBackend()
    backend.enqueue(reportResponse(2, 'queued'))
    backend.enqueueStatuses(statusResponse(2, 'queued'))
    const session = createLiveInvestigationSession('incident-1', backend)

    session.start()
    await settle()
    expect(session.getSnapshot()).toMatchObject({ report: { version: 2, status: 'queued' }, connected: true })

    const delayedReport = deferred<InvestigationReportResponse>()
    const delayedStatus = deferred<IncidentStatusResponse>()
    backend.enqueue(delayedReport.promise)
    backend.enqueueStatuses(delayedStatus.promise)
    const statusReadsBeforeReload = backend.statusReads
    const reload = session.reload()
    await settle()

    backend.connection.emitStatusChanged(2, 'collecting')
    delayedReport.resolve(reportResponse(2, 'queued'))
    await settle()

    expect(session.getSnapshot().report).toMatchObject({ version: 2, status: 'collecting' })
    expect(backend.statusReads).toBe(statusReadsBeforeReload + 1)

    backend.connection.emitStatusChanged(2, 'synthesizing')
    delayedStatus.resolve(statusResponse(2, 'collecting'))
    await reload
    expect(session.getSnapshot().report).toMatchObject({ version: 2, status: 'synthesizing' })
    await session.dispose()
  })

  it('catches up a newer live status without using its version as the report ETag', async () => {
    const backend = new FakeBackend()
    backend.connection.startError = new Error('offline')
    backend.enqueue(reportResponse(1, 'queued'))
    backend.enqueueStatuses(statusResponse(1, 'queued'))
    const session = createLiveInvestigationSession('incident-1', backend)

    session.start()
    await settle()

    const currentRead = deferred<InvestigationReportResponse>()
    backend.enqueue(currentRead.promise, reportResponse(2, 'queued'))
    backend.enqueueStatuses(statusResponse(2, 'collecting'), statusResponse(2, 'collecting'))
    const reload = session.reload()
    await settle()

    backend.connection.emitStatusChanged(2, 'collecting')
    currentRead.resolve({ status: 304 })
    await reload

    expect(backend.requestedVersions.slice(-2)).toEqual([1, 1])
    expect(session.getSnapshot().report).toMatchObject({ version: 2, status: 'collecting' })
    await session.dispose()
  })

  it('reconciles a status-only change through polling after the live event was missed', async () => {
    const backend = new FakeBackend()
    backend.connection.startError = new Error('offline')
    backend.enqueue(reportResponse(2, 'queued'))
    backend.enqueueStatuses(statusResponse(2, 'queued'))
    const session = createLiveInvestigationSession('incident-1', backend)

    session.start()
    await settle()
    expect(session.getSnapshot()).toMatchObject({ report: { version: 2, status: 'queued' }, connected: false })

    backend.enqueue({ status: 304 })
    backend.enqueueStatuses(statusResponse(2, 'collecting'))
    backend.poll()
    await settle()

    expect(backend.requestedVersions.at(-1)).toBe(2)
    expect(session.getSnapshot().report).toMatchObject({ version: 2, status: 'collecting' })
    await session.dispose()
  })

  it('uses versions as ETags and handles pending, not-modified, missing, failed, and report responses', async () => {
    const backend = new FakeBackend()
    backend.connection.startError = new Error('offline')
    backend.enqueue(pendingResponse(1))
    const session = createLiveInvestigationSession('incident-1', backend)

    session.start()
    await settle()
    expect(session.getSnapshot()).toMatchObject({ report: null, error: null })
    expect(backend.requestedVersions).toEqual([0])

    backend.enqueue({ status: 304 })
    await session.reload()
    expect(backend.requestedVersions.at(-1)).toBe(1)

    backend.enqueue({ status: 404 })
    await session.reload()
    expect(session.getSnapshot().error).toBe('This investigation does not exist or has expired.')

    backend.enqueue({ status: 304 })
    await session.reload()
    expect(session.getSnapshot().error).toBeNull()

    backend.enqueue({ status: 503 })
    await session.reload()
    expect(session.getSnapshot().error).toBe('Report request failed (503).')

    backend.enqueue(reportResponse(2))
    await session.reload()
    expect(session.getSnapshot()).toMatchObject({ report: { version: 2 }, error: null })
    await session.dispose()
  })

  it('keeps polling active when the initial group join fails', async () => {
    const backend = new FakeBackend()
    backend.connection.joinResults.push(new Error('join failed'))
    backend.enqueue(reportResponse(1))
    const session = createLiveInvestigationSession('incident-1', backend)

    session.start()
    await settle()

    expect(session.getSnapshot()).toMatchObject({ report: { version: 1 }, connected: false })
    expect(backend.activePolling).toBe(1)
    expect(backend.connection.joinedIncidentIds).toEqual(['incident-1'])
    await session.dispose()
  })

  it('resumes polling when a reconnect cannot rejoin the incident group', async () => {
    const backend = new FakeBackend()
    backend.enqueue(reportResponse(1))
    const session = createLiveInvestigationSession('incident-1', backend)

    session.start()
    await settle()
    expect(session.getSnapshot().connected).toBe(true)
    expect(backend.activePolling).toBe(0)

    backend.connection.joinResults.push(new Error('rejoin failed'))
    backend.connection.emitReconnecting()
    backend.connection.emitReconnected()
    await settle()

    expect(session.getSnapshot().connected).toBe(false)
    expect(backend.activePolling).toBe(1)

    backend.enqueue(reportResponse(2))
    backend.poll()
    await settle()
    expect(session.getSnapshot().report?.version).toBe(2)
    await session.dispose()
  })

  it('keeps polling after a successful rejoin until a later catch-up succeeds', async () => {
    const backend = new FakeBackend()
    backend.enqueue(reportResponse(1))
    backend.enqueueStatuses(statusResponse(1))
    const session = createLiveInvestigationSession('incident-1', backend)

    session.start()
    await settle()
    expect(session.getSnapshot().connected).toBe(true)

    const failedCatchUp = deferred<InvestigationReportResponse>()
    backend.enqueue(failedCatchUp.promise)
    backend.connection.emitReconnecting()
    backend.connection.emitReconnected()
    await settle()
    failedCatchUp.reject(new Error('catch-up failed'))
    await settle()

    expect(session.getSnapshot().connected).toBe(false)
    expect(session.getSnapshot().error).toBe('catch-up failed')
    expect(backend.activePolling).toBe(1)

    backend.enqueue(reportResponse(2))
    backend.enqueueStatuses(statusResponse(2))
    backend.poll()
    await settle()

    expect(session.getSnapshot()).toMatchObject({ report: { version: 2 }, connected: true, error: null })
    expect(backend.activePolling).toBe(0)
    await session.dispose()
  })

  it('polls while a joined session is pending and stops after a report arrives', async () => {
    const backend = new FakeBackend()
    backend.enqueue(pendingResponse(1))
    const session = createLiveInvestigationSession('incident-1', backend)

    session.start()
    await settle()

    expect(session.getSnapshot()).toMatchObject({ report: null, connected: true })
    expect(backend.activePolling).toBe(1)
    expect(backend.pollingIntervals).toEqual([5000])

    backend.enqueue(reportResponse(2))
    backend.poll()
    await settle()

    expect(session.getSnapshot().report?.version).toBe(2)
    expect(backend.activePolling).toBe(0)
    await session.dispose()
  })

  it('ignores stale live versions and reloads for a newer notification', async () => {
    const backend = new FakeBackend()
    backend.enqueue(reportResponse(2))
    const session = createLiveInvestigationSession('incident-1', backend)

    session.start()
    await settle()
    const readsAfterJoin = backend.requestedVersions.length

    backend.connection.emitUpdated(2)
    await settle()
    expect(backend.requestedVersions).toHaveLength(readsAfterJoin)

    backend.enqueue(reportResponse(3))
    backend.connection.emitUpdated(3)
    await settle()
    expect(session.getSnapshot().report?.version).toBe(3)
    expect(backend.requestedVersions.at(-1)).toBe(2)
    await session.dispose()
  })

  it('applies equal-version status changes locally and ignores stale status changes', async () => {
    const backend = new FakeBackend()
    backend.enqueue(reportResponse(2, 'queued'))
    const session = createLiveInvestigationSession('incident-1', backend)

    session.start()
    await settle()
    const readsAfterJoin = backend.requestedVersions.length

    backend.connection.emitStatusChanged(2, 'collecting')
    await settle()
    expect(session.getSnapshot().report).toMatchObject({ version: 2, status: 'collecting' })
    expect(backend.requestedVersions).toHaveLength(readsAfterJoin)

    backend.connection.emitStatusChanged(1, 'failed')
    await settle()
    expect(session.getSnapshot().report?.status).toBe('collecting')
    expect(backend.requestedVersions).toHaveLength(readsAfterJoin)
    await session.dispose()
  })

  it('aborts reads, cancels polling, stops live updates, and ignores work after cleanup', async () => {
    const backend = new FakeBackend()
    backend.connection.startError = new Error('offline')
    const pendingRead = deferred<InvestigationReportResponse>()
    backend.enqueue(pendingRead.promise)
    const session = createLiveInvestigationSession('incident-1', backend)
    const snapshots: number[] = []
    session.subscribe((snapshot) => snapshots.push(snapshot.report?.version ?? 0))

    session.start()
    await settle()
    const readsBeforeCleanup = backend.requestedVersions.length
    await session.dispose()

    expect(backend.activePolling).toBe(0)
    expect(backend.connection.stopCalls).toBe(1)
    expect(backend.signals[0]?.aborted).toBe(true)

    pendingRead.resolve(reportResponse(4))
    backend.connection.emitUpdated(5)
    backend.poll()
    await settle()

    expect(session.getSnapshot().report).toBeNull()
    expect(backend.requestedVersions).toHaveLength(readsBeforeCleanup)
    expect(snapshots).toEqual([0])
  })
})

class FakeBackend implements LiveInvestigationBackend {
  readonly connection = new FakeConnection()
  readonly requestedVersions: number[] = []
  readonly signals: AbortSignal[] = []
  readonly pollingIntervals: number[] = []
  statusReads = 0
  maxConcurrentReportReads = 0
  private readonly responses: Array<InvestigationReportResponse | Promise<InvestigationReportResponse>> = []
  private readonly statusResponses: Array<IncidentStatusResponse | Promise<IncidentStatusResponse>> = []
  private readonly polls = new Map<number, () => void>()
  private nextPollId = 1
  private activeReportReads = 0
  private lastReportVersion = 0
  private lastReportStatus = 'queued'

  get activePolling() {
    return this.polls.size
  }

  enqueue(...responses: Array<InvestigationReportResponse | Promise<InvestigationReportResponse>>) {
    this.responses.push(...responses)
  }

  enqueueStatuses(...responses: Array<IncidentStatusResponse | Promise<IncidentStatusResponse>>) {
    this.statusResponses.push(...responses)
  }

  async loadReport(_incidentId: string, version: number, signal: AbortSignal) {
    this.requestedVersions.push(version)
    this.signals.push(signal)
    this.activeReportReads++
    this.maxConcurrentReportReads = Math.max(this.maxConcurrentReportReads, this.activeReportReads)
    try {
      const response = await (this.responses.shift() ?? { status: 304 })
      if (response.body) {
        this.lastReportVersion = response.body.version
        this.lastReportStatus = response.body.status
      }
      return response
    } finally {
      this.activeReportReads--
    }
  }

  async loadStatus(_incidentId: string, signal: AbortSignal) {
    this.statusReads++
    this.signals.push(signal)
    return await (this.statusResponses.shift()
      ?? statusResponse(this.lastReportVersion, this.lastReportStatus))
  }

  createLiveConnection(handlers: LiveInvestigationHandlers) {
    this.connection.handlers = handlers
    return this.connection
  }

  repeat(callback: () => void, intervalMs: number) {
    const id = this.nextPollId++
    this.pollingIntervals.push(intervalMs)
    this.polls.set(id, callback)
    return () => void this.polls.delete(id)
  }

  poll() {
    for (const callback of [...this.polls.values()]) callback()
  }
}

class FakeConnection implements LiveInvestigationConnection {
  handlers: LiveInvestigationHandlers | null = null
  startError: Error | null = null
  readonly joinResults: Array<Error | null> = []
  readonly joinedIncidentIds: string[] = []
  stopCalls = 0

  async start() {
    if (this.startError) throw this.startError
  }

  async join(incidentId: string) {
    this.joinedIncidentIds.push(incidentId)
    const result = this.joinResults.shift()
    if (result) throw result
  }

  async stop() {
    this.stopCalls++
  }

  emitUpdated(version: number) {
    this.handlers?.updated({ version } as IncidentUpdated)
  }

  emitStatusChanged(version: number, status: string) {
    this.handlers?.statusChanged({ version, status } as IncidentStatusChanged)
  }

  emitReconnecting() {
    this.handlers?.reconnecting()
  }

  emitReconnected() {
    this.handlers?.reconnected()
  }
}

function reportResponse(version: number, status = 'completed'): InvestigationReportResponse {
  return { status: 200, body: { version, status } as InvestigationReport }
}

function pendingResponse(version: number, status = 'queued'): InvestigationReportResponse {
  return { status: 202, body: { version, status } as IncidentPending }
}

function statusResponse(version: number, status = 'completed'): IncidentStatusResponse {
  return { status: 200, body: { version, status } as IncidentStatus }
}

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((complete, fail) => {
    resolve = complete
    reject = fail
  })
  return { promise, resolve, reject }
}

async function settle() {
  for (let index = 0; index < 20; index++) await Promise.resolve()
}
