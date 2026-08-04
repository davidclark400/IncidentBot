import { describe, expect, it } from 'vitest'
import type {
  CasePending,
  CaseStatus,
  CaseStatusChanged,
  CaseUpdated,
} from '../../case'
import type { CaseFile } from '../../caseFile'
import type { CaseProgress } from '../../caseProgress'
import {
  createLiveCase,
  type CaseFileResponse,
  type CaseStatusResponse,
  type LiveCaseConnection,
  type LiveCaseHandlers,
  type LiveCaseBackend,
} from './liveCase'

describe('Live Case', () => {
  it('serializes and coalesces reloads while a read is in flight', async () => {
    const backend = new FakeBackend()
    backend.connection.startError = new Error('offline')
    const first = deferred<CaseFileResponse>()
    backend.enqueue(first.promise, caseFileResponse(2))
    backend.enqueueStatuses(statusResponse(1), statusResponse(2))
    const liveCase = createLiveCase('case-1', backend)

    liveCase.start()
    await settle()
    const firstReload = liveCase.reload()
    const secondReload = liveCase.reload()

    expect(backend.requestedVersions).toEqual([0])
    expect(backend.maxConcurrentCaseFileReads).toBe(1)

    first.resolve(caseFileResponse(1))
    await Promise.all([firstReload, secondReload])

    expect(liveCase.getSnapshot().caseFile?.caseFileVersion).toBe(2)
    expect(backend.requestedVersions).toEqual([0, 1])
    expect(backend.maxConcurrentCaseFileReads).toBe(1)
    await liveCase.dispose()
  })

  it('retains a live status when an in-flight same-version caseFile has an older status', async () => {
    const backend = new FakeBackend()
    backend.enqueue(caseFileResponse(2, 'queued'))
    backend.enqueueStatuses(statusResponse(2, 'queued'))
    const liveCase = createLiveCase('case-1', backend)

    liveCase.start()
    await settle()
    expect(liveCase.getSnapshot()).toMatchObject({ caseFile: { caseFileVersion: 2, status: 'queued' }, connected: true })

    const delayedCaseFile = deferred<CaseFileResponse>()
    const delayedStatus = deferred<CaseStatusResponse>()
    backend.enqueue(delayedCaseFile.promise)
    backend.enqueueStatuses(delayedStatus.promise)
    const statusReadsBeforeReload = backend.statusReads
    const reload = liveCase.reload()
    await settle()

    backend.connection.emitStatusChanged(2, 'collecting')
    delayedCaseFile.resolve(caseFileResponse(2, 'queued'))
    await settle()

    expect(liveCase.getSnapshot().caseFile).toMatchObject({ caseFileVersion: 2, status: 'collecting' })
    expect(backend.statusReads).toBe(statusReadsBeforeReload + 1)

    backend.connection.emitStatusChanged(2, 'synthesizing')
    delayedStatus.resolve(statusResponse(2, 'collecting'))
    await reload
    expect(liveCase.getSnapshot().caseFile).toMatchObject({ caseFileVersion: 2, status: 'synthesizing' })
    await liveCase.dispose()
  })

  it('catches up a newer live status without using its version as the caseFile ETag', async () => {
    const backend = new FakeBackend()
    backend.connection.startError = new Error('offline')
    backend.enqueue(caseFileResponse(1, 'queued'))
    backend.enqueueStatuses(statusResponse(1, 'queued'))
    const liveCase = createLiveCase('case-1', backend)

    liveCase.start()
    await settle()

    const currentRead = deferred<CaseFileResponse>()
    backend.enqueue(currentRead.promise, caseFileResponse(2, 'queued'))
    backend.enqueueStatuses(statusResponse(2, 'collecting'), statusResponse(2, 'collecting'))
    const reload = liveCase.reload()
    await settle()

    backend.connection.emitStatusChanged(2, 'collecting')
    currentRead.resolve({ status: 304 })
    await reload

    expect(backend.requestedVersions.slice(-2)).toEqual([1, 1])
    expect(liveCase.getSnapshot().caseFile).toMatchObject({ caseFileVersion: 2, status: 'collecting' })
    await liveCase.dispose()
  })

  it('reconciles a status-only change through polling after the live event was missed', async () => {
    const backend = new FakeBackend()
    backend.connection.startError = new Error('offline')
    backend.enqueue(caseFileResponse(2, 'queued'))
    backend.enqueueStatuses(statusResponse(2, 'queued'))
    const liveCase = createLiveCase('case-1', backend)

    liveCase.start()
    await settle()
    expect(liveCase.getSnapshot()).toMatchObject({ caseFile: { caseFileVersion: 2, status: 'queued' }, connected: false })

    backend.enqueue({ status: 304 })
    backend.enqueueStatuses(statusResponse(2, 'collecting'))
    backend.poll()
    await settle()

    expect(backend.requestedVersions.at(-1)).toBe(2)
    expect(liveCase.getSnapshot().caseFile).toMatchObject({ caseFileVersion: 2, status: 'collecting' })
    await liveCase.dispose()
  })

  it('loads persisted progress, ignores stale revisions, and clears it when status authoritatively returns null', async () => {
    const backend = new FakeBackend()
    backend.connection.startError = new Error('offline')
    const persisted = progressProjection({ revision: 3, currentPass: 2, currentLookbackMinutes: 120 })
    backend.enqueue(caseFileResponse(2, 'collecting'))
    backend.enqueueStatuses(statusResponse(2, 'collecting', 0, 0, persisted))
    const liveCase = createLiveCase('case-1', backend)

    liveCase.start()
    await settle()
    expect(liveCase.getSnapshot().progress).toMatchObject({
      revision: 3,
      currentPass: 2,
      currentLookbackMinutes: 120,
    })

    backend.enqueue({ status: 304 })
    backend.enqueueStatuses(statusResponse(2, 'collecting', 0, 0, progressProjection({ revision: 2 })))
    await liveCase.reload()

    expect(liveCase.getSnapshot().progress).toMatchObject({ revision: 3 })

    backend.enqueue({ status: 304 })
    backend.enqueueStatuses(statusResponse(2, 'collecting', 0, 0, null))
    await liveCase.reload()

    expect(liveCase.getSnapshot().progress).toBeNull()
    await liveCase.dispose()
  })

  it('accepts dedicated progress events monotonically across revisions and attempts', async () => {
    const backend = new FakeBackend()
    backend.connection.startError = new Error('offline')
    backend.enqueue(caseFileResponse(2, 'collecting'))
    backend.enqueueStatuses(statusResponse(2, 'collecting'))
    const liveCase = createLiveCase('case-1', backend)

    liveCase.start()
    await settle()

    backend.connection.emitProgress(progressProjection({ revision: 1 }))
    backend.connection.emitProgress(progressProjection({ revision: 2, currentPass: 2 }))
    backend.connection.emitProgress(progressProjection({ revision: 1, currentPass: 1 }))
    backend.connection.emitProgress(progressProjection({
      attemptId: 'attempt-2',
      revision: 1,
      startedAt: '2026-08-03T10:01:00Z',
      updatedAt: '2026-08-03T10:01:01Z',
      currentPass: 3,
    }))
    backend.connection.emitProgress(progressProjection({
      attemptId: 'attempt-1',
      revision: 99,
      updatedAt: '2026-08-03T10:02:00Z',
      currentPass: 99,
    }))
    backend.connection.emitProgress(progressProjection({
      attemptId: 'attempt-3',
      baseCaseFileVersion: 1,
      revision: 10,
      startedAt: '2026-08-03T10:03:00Z',
      currentPass: 10,
    }))
    backend.connection.emitProgress(progressProjection({ caseId: 'another-case', revision: 100 }))
    await settle()

    expect(liveCase.getSnapshot().progress).toMatchObject({
      attemptId: 'attempt-2',
      revision: 1,
      currentPass: 3,
    })

    const nextBase = progressProjection({
      attemptId: 'attempt-4',
      baseCaseFileVersion: 3,
      revision: 1,
      startedAt: '2026-08-03T10:04:00Z',
      updatedAt: '2026-08-03T10:04:01Z',
      currentPass: 4,
    })
    backend.enqueue(caseFileResponse(3, 'collecting'))
    backend.enqueueStatuses(statusResponse(3, 'collecting', 0, 0, nextBase))
    backend.connection.emitProgress(nextBase)
    await settle()

    expect(liveCase.getSnapshot()).toMatchObject({
      caseFile: { caseFileVersion: 3 },
      progress: { baseCaseFileVersion: 3, attemptId: 'attempt-4', revision: 1 },
    })
    await liveCase.dispose()
  })

  it('does not let an in-flight null status response erase newer live progress', async () => {
    const backend = new FakeBackend()
    backend.connection.startError = new Error('offline')
    backend.enqueue(caseFileResponse(2, 'collecting'))
    backend.enqueueStatuses(statusResponse(2, 'collecting', 0, 0, progressProjection({ revision: 1 })))
    const liveCase = createLiveCase('case-1', backend)

    liveCase.start()
    await settle()

    const delayedStatus = deferred<CaseStatusResponse>()
    backend.enqueue({ status: 304 })
    backend.enqueueStatuses(delayedStatus.promise)
    const reload = liveCase.reload()
    await settle()

    backend.connection.emitProgress(progressProjection({ revision: 2, currentPass: 2 }))
    delayedStatus.resolve(statusResponse(2, 'collecting', 0, 0, null))
    await reload

    expect(liveCase.getSnapshot().progress).toMatchObject({ revision: 2, currentPass: 2 })
    await liveCase.dispose()
  })

  it('removes progress as soon as a newer canonical caseFile version loads', async () => {
    const backend = new FakeBackend()
    backend.connection.startError = new Error('offline')
    backend.enqueue(caseFileResponse(2, 'collecting'))
    backend.enqueueStatuses(statusResponse(2, 'collecting', 0, 0, progressProjection({ revision: 4 })))
    const liveCase = createLiveCase('case-1', backend)

    liveCase.start()
    await settle()

    const delayedStatus = deferred<CaseStatusResponse>()
    backend.enqueue(caseFileResponse(3, 'completed'))
    backend.enqueueStatuses(delayedStatus.promise)
    const reload = liveCase.reload()
    await settle()

    expect(liveCase.getSnapshot()).toMatchObject({
      caseFile: { caseFileVersion: 3, status: 'completed' },
      progress: null,
    })

    delayedStatus.resolve(statusResponse(3, 'completed'))
    await reload
    await liveCase.dispose()
  })

  it('uses versions as ETags and handles pending, not-modified, missing, failed, and caseFile responses', async () => {
    const backend = new FakeBackend()
    backend.connection.startError = new Error('offline')
    backend.enqueue(casePendingResponse(1))
    const liveCase = createLiveCase('case-1', backend)

    liveCase.start()
    await settle()
    expect(liveCase.getSnapshot()).toMatchObject({ caseFile: null, error: null })
    expect(backend.requestedVersions).toEqual([0])

    backend.enqueue({ status: 304 })
    await liveCase.reload()
    expect(backend.requestedVersions.at(-1)).toBe(1)

    backend.enqueue({ status: 404 })
    await liveCase.reload()
    expect(liveCase.getSnapshot().error).toBe('This Case does not exist or has expired.')

    backend.enqueue({ status: 304 })
    await liveCase.reload()
    expect(liveCase.getSnapshot().error).toBeNull()

    backend.enqueue({ status: 503 })
    await liveCase.reload()
    expect(liveCase.getSnapshot().error).toBe('Case File request failed (503).')

    backend.enqueue(caseFileResponse(2))
    await liveCase.reload()
    expect(liveCase.getSnapshot()).toMatchObject({ caseFile: { caseFileVersion: 2 }, error: null })
    await liveCase.dispose()
  })

  it('keeps polling active when the initial group join fails', async () => {
    const backend = new FakeBackend()
    backend.connection.joinResults.push(new Error('join failed'))
    backend.enqueue(caseFileResponse(1))
    const liveCase = createLiveCase('case-1', backend)

    liveCase.start()
    await settle()

    expect(liveCase.getSnapshot()).toMatchObject({ caseFile: { caseFileVersion: 1 }, connected: false })
    expect(backend.activePolling).toBe(1)
    expect(backend.connection.joinedCaseIds).toEqual(['case-1'])
    await liveCase.dispose()
  })

  it('resumes polling when a reconnect cannot rejoin the case group', async () => {
    const backend = new FakeBackend()
    backend.enqueue(caseFileResponse(1))
    const liveCase = createLiveCase('case-1', backend)

    liveCase.start()
    await settle()
    expect(liveCase.getSnapshot().connected).toBe(true)
    expect(backend.activePolling).toBe(0)

    backend.connection.joinResults.push(new Error('rejoin failed'))
    backend.connection.emitReconnecting()
    backend.connection.emitReconnected()
    await settle()

    expect(liveCase.getSnapshot().connected).toBe(false)
    expect(backend.activePolling).toBe(1)

    backend.enqueue(caseFileResponse(2))
    backend.poll()
    await settle()
    expect(liveCase.getSnapshot().caseFile?.caseFileVersion).toBe(2)
    await liveCase.dispose()
  })

  it('keeps polling after a successful rejoin until a later catch-up succeeds', async () => {
    const backend = new FakeBackend()
    backend.enqueue(caseFileResponse(1))
    backend.enqueueStatuses(statusResponse(1))
    const liveCase = createLiveCase('case-1', backend)

    liveCase.start()
    await settle()
    expect(liveCase.getSnapshot().connected).toBe(true)

    const failedCatchUp = deferred<CaseFileResponse>()
    backend.enqueue(failedCatchUp.promise)
    backend.connection.emitReconnecting()
    backend.connection.emitReconnected()
    await settle()
    failedCatchUp.reject(new Error('catch-up failed'))
    await settle()

    expect(liveCase.getSnapshot().connected).toBe(false)
    expect(liveCase.getSnapshot().error).toBe('catch-up failed')
    expect(backend.activePolling).toBe(1)

    backend.enqueue(caseFileResponse(2))
    backend.enqueueStatuses(statusResponse(2))
    backend.poll()
    await settle()

    expect(liveCase.getSnapshot()).toMatchObject({ caseFile: { caseFileVersion: 2 }, connected: true, error: null })
    expect(backend.activePolling).toBe(0)
    await liveCase.dispose()
  })

  it('polls while a joined liveCase is pending and stops after a caseFile arrives', async () => {
    const backend = new FakeBackend()
    backend.enqueue(casePendingResponse(1))
    const liveCase = createLiveCase('case-1', backend)

    liveCase.start()
    await settle()

    expect(liveCase.getSnapshot()).toMatchObject({ caseFile: null, connected: true })
    expect(backend.activePolling).toBe(1)
    expect(backend.pollingIntervals).toEqual([5000])

    backend.enqueue(caseFileResponse(2))
    backend.poll()
    await settle()

    expect(liveCase.getSnapshot().caseFile?.caseFileVersion).toBe(2)
    expect(backend.activePolling).toBe(0)
    await liveCase.dispose()
  })

  it('ignores stale live versions and reloads for a newer notification', async () => {
    const backend = new FakeBackend()
    backend.enqueue(caseFileResponse(2))
    const liveCase = createLiveCase('case-1', backend)

    liveCase.start()
    await settle()
    const readsAfterJoin = backend.requestedVersions.length

    backend.connection.emitUpdated(2)
    await settle()
    expect(backend.requestedVersions).toHaveLength(readsAfterJoin)

    backend.enqueue(caseFileResponse(3))
    backend.connection.emitUpdated(3)
    await settle()
    expect(liveCase.getSnapshot().caseFile?.caseFileVersion).toBe(3)
    expect(backend.requestedVersions.at(-1)).toBe(2)
    await liveCase.dispose()
  })

  it('applies same-caseFile-version input progress from live notifications', async () => {
    const backend = new FakeBackend()
    backend.enqueue(caseFileResponse(2, 'ready', 4, 4))
    backend.enqueueStatuses(statusResponse(2, 'ready', 4, 4))
    const liveCase = createLiveCase('case-1', backend)

    liveCase.start()
    await settle()
    const readsAfterJoin = backend.requestedVersions.length

    backend.connection.emitUpdated(2, 5, 4)
    await settle()

    expect(liveCase.getSnapshot().caseFile).toMatchObject({
      caseFileVersion: 2,
      inputVersion: 5,
      projectedInputVersion: 4,
    })
    expect(backend.requestedVersions).toHaveLength(readsAfterJoin)
    await liveCase.dispose()
  })

  it('applies equal-version status changes locally and ignores stale status changes', async () => {
    const backend = new FakeBackend()
    backend.enqueue(caseFileResponse(2, 'queued'))
    const liveCase = createLiveCase('case-1', backend)

    liveCase.start()
    await settle()
    const readsAfterJoin = backend.requestedVersions.length

    backend.connection.emitStatusChanged(2, 'collecting')
    await settle()
    expect(liveCase.getSnapshot().caseFile).toMatchObject({ caseFileVersion: 2, status: 'collecting' })
    expect(backend.requestedVersions).toHaveLength(readsAfterJoin)

    backend.connection.emitStatusChanged(1, 'failed')
    await settle()
    expect(liveCase.getSnapshot().caseFile?.status).toBe('collecting')
    expect(backend.requestedVersions).toHaveLength(readsAfterJoin)
    await liveCase.dispose()
  })

  it('aborts reads, cancels polling, stops live updates, and ignores work after cleanup', async () => {
    const backend = new FakeBackend()
    backend.connection.startError = new Error('offline')
    const pendingRead = deferred<CaseFileResponse>()
    backend.enqueue(pendingRead.promise)
    const liveCase = createLiveCase('case-1', backend)
    const snapshots: number[] = []
    liveCase.subscribe((snapshot) => snapshots.push(snapshot.caseFile?.caseFileVersion ?? 0))

    liveCase.start()
    await settle()
    const readsBeforeCleanup = backend.requestedVersions.length
    await liveCase.dispose()

    expect(backend.activePolling).toBe(0)
    expect(backend.connection.stopCalls).toBe(1)
    expect(backend.signals[0]?.aborted).toBe(true)

    pendingRead.resolve(caseFileResponse(4))
    backend.connection.emitUpdated(5)
    backend.poll()
    await settle()

    expect(liveCase.getSnapshot().caseFile).toBeNull()
    expect(backend.requestedVersions).toHaveLength(readsBeforeCleanup)
    expect(snapshots).toEqual([0])
  })
})

class FakeBackend implements LiveCaseBackend {
  readonly connection = new FakeConnection()
  readonly requestedVersions: number[] = []
  readonly signals: AbortSignal[] = []
  readonly pollingIntervals: number[] = []
  statusReads = 0
  maxConcurrentCaseFileReads = 0
  private readonly responses: Array<CaseFileResponse | Promise<CaseFileResponse>> = []
  private readonly statusResponses: Array<CaseStatusResponse | Promise<CaseStatusResponse>> = []
  private readonly polls = new Map<number, () => void>()
  private nextPollId = 1
  private activeCaseFileReads = 0
  private lastCaseFileVersion = 0
  private lastCaseFileStatus = 'queued'

  get activePolling() {
    return this.polls.size
  }

  enqueue(...responses: Array<CaseFileResponse | Promise<CaseFileResponse>>) {
    this.responses.push(...responses)
  }

  enqueueStatuses(...responses: Array<CaseStatusResponse | Promise<CaseStatusResponse>>) {
    this.statusResponses.push(...responses)
  }

  async loadCaseFile(_caseId: string, caseFileVersion: number, signal: AbortSignal) {
    this.requestedVersions.push(caseFileVersion)
    this.signals.push(signal)
    this.activeCaseFileReads++
    this.maxConcurrentCaseFileReads = Math.max(this.maxConcurrentCaseFileReads, this.activeCaseFileReads)
    try {
      const response = await (this.responses.shift() ?? { status: 304 })
      if (response.body) {
        this.lastCaseFileVersion = response.body.caseFileVersion
        this.lastCaseFileStatus = response.body.status
      }
      return response
    } finally {
      this.activeCaseFileReads--
    }
  }

  async loadStatus(_caseId: string, signal: AbortSignal) {
    this.statusReads++
    this.signals.push(signal)
    return await (this.statusResponses.shift()
      ?? statusResponse(this.lastCaseFileVersion, this.lastCaseFileStatus))
  }

  createLiveConnection(handlers: LiveCaseHandlers) {
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

class FakeConnection implements LiveCaseConnection {
  handlers: LiveCaseHandlers | null = null
  startError: Error | null = null
  readonly joinResults: Array<Error | null> = []
  readonly joinedCaseIds: string[] = []
  stopCalls = 0

  async start() {
    if (this.startError) throw this.startError
  }

  async join(caseId: string) {
    this.joinedCaseIds.push(caseId)
    const result = this.joinResults.shift()
    if (result) throw result
  }

  async stop() {
    this.stopCalls++
  }

  emitUpdated(caseFileVersion: number, inputVersion?: number, projectedInputVersion?: number) {
    this.handlers?.updated({ caseFileVersion, inputVersion, projectedInputVersion } as unknown as CaseUpdated)
  }

  emitStatusChanged(caseFileVersion: number, status: string) {
    this.handlers?.statusChanged({ caseFileVersion, status } as unknown as CaseStatusChanged)
  }

  emitProgress(progress: CaseProgress) {
    this.handlers?.progressUpdated(progress)
  }

  emitReconnecting() {
    this.handlers?.reconnecting()
  }

  emitReconnected() {
    this.handlers?.reconnected()
  }
}

function caseFileResponse(caseFileVersion: number, status = 'completed', inputVersion = 0, projectedInputVersion = inputVersion): CaseFileResponse {
  return { status: 200, body: { caseFileVersion, status, inputVersion, projectedInputVersion } as unknown as CaseFile }
}

function casePendingResponse(caseFileVersion: number, status = 'queued'): CaseFileResponse {
  return { status: 202, body: { caseFileVersion, status } as unknown as CasePending }
}

function statusResponse(
  caseFileVersion: number,
  status = 'completed',
  inputVersion = 0,
  projectedInputVersion = inputVersion,
  progress: CaseProgress | null = null,
): CaseStatusResponse {
  return {
    status: 200,
    body: { caseFileVersion, status, inputVersion, projectedInputVersion, progress } as unknown as CaseStatus,
  }
}

function progressProjection(overrides: Partial<CaseProgress> = {}): CaseProgress {
  return {
    caseId: 'case-1',
    attemptId: 'attempt-1',
    revision: 1,
    baseCaseFileVersion: 2,
    startedAt: '2026-08-03T10:00:00Z',
    updatedAt: '2026-08-03T10:00:01Z',
    elapsedDurationMilliseconds: 1000,
    phase: 'collecting',
    currentPass: 1,
    currentLookbackMinutes: 60,
    deterministicCaseFileUsable: false,
    onlyAiSynthesisRemaining: false,
    aiSynthesisState: 'pending',
    crumbSources: [],
    earlyCrumbs: [],
    ...overrides,
  }
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
