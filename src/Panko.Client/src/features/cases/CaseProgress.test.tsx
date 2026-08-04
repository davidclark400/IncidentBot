import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import type { CaseProgress } from '../../caseProgress'
import { CaseProgressPanel } from './CaseProgress'
import { formatProgressDuration, sourceDisplayName } from './caseProgressPresentation'

describe('Case progress panel', () => {
  it('shows adaptive coverage, source outcomes, readiness, synthesis, and early Crumbs', () => {
    const markup = renderToStaticMarkup(<CaseProgressPanel progress={projection} />)

    expect(markup).toContain('Pass 2 · 120-minute coverage')
    expect(markup).toContain('Elapsed 1.8 s')
    expect(markup).toContain('Deterministic Case File usable')
    expect(markup).toContain('Only AI synthesis remains')
    expect(markup).toContain('AI synthesis running')
    expect(markup).toContain('PagerDuty')
    expect(markup).toContain('Received')
    expect(markup).toContain('420 ms')
    expect(markup).toContain('VictoriaLogs')
    expect(markup).toContain('Querying')
    expect(markup).toContain('pass 2 / 120-minute coverage')
    expect(markup).toContain('Grafana')
    expect(markup).toContain('Timed out')
    expect(markup).toContain('Nomad')
    expect(markup).toContain('Excluded')
    expect(markup).toContain('Database latency crossed the alert threshold')
  })

  it('formats responder-facing durations and source names', () => {
    expect(formatProgressDuration(420)).toBe('420 ms')
    expect(formatProgressDuration(1800)).toBe('1.8 s')
    expect(formatProgressDuration(60_000)).toBe('1 min')
    expect(formatProgressDuration(65_000)).toBe('1 min 5 s')
    expect(sourceDisplayName('pagerduty')).toBe('PagerDuty')
    expect(sourceDisplayName('victorialogs')).toBe('VictoriaLogs')
  })

  it('shows the final canonical commit window after synthesis finishes', () => {
    const markup = renderToStaticMarkup(<CaseProgressPanel progress={{
      ...projection,
      phase: 'finalizing',
      onlyAiSynthesisRemaining: false,
      aiSynthesisState: 'complete',
    }} />)

    expect(markup).toContain('Publishing the Case File')
    expect(markup).toContain('AI synthesis complete')
    expect(markup).not.toContain('Only AI synthesis remains')
  })
})

const projection: CaseProgress = {
  caseId: 'case-1',
  attemptId: 'attempt-1',
  revision: 7,
  baseCaseFileVersion: 2,
  startedAt: '2026-08-03T10:00:00Z',
  updatedAt: '2026-08-03T10:00:01.800Z',
  elapsedDurationMilliseconds: 1800,
  phase: 'synthesizing',
  currentPass: 2,
  currentLookbackMinutes: 120,
  deterministicCaseFileUsable: true,
  onlyAiSynthesisRemaining: true,
  aiSynthesisState: 'running',
  crumbSources: [
    {
      source: 'pagerduty',
      requestState: 'received',
      health: 'complete',
      pass: 1,
      lookbackMinutes: 60,
      durationMilliseconds: 420,
      crumbCount: 1,
      diagnostic: null,
      startedAt: '2026-08-03T10:00:00Z',
      updatedAt: '2026-08-03T10:00:00.420Z',
    },
    {
      source: 'victorialogs',
      requestState: 'querying',
      health: 'pending',
      pass: 2,
      lookbackMinutes: 120,
      durationMilliseconds: 900,
      crumbCount: 0,
      diagnostic: null,
      startedAt: '2026-08-03T10:00:00.900Z',
      updatedAt: '2026-08-03T10:00:01.800Z',
    },
    {
      source: 'grafana',
      requestState: 'timedOut',
      health: 'unavailable',
      pass: 1,
      lookbackMinutes: 60,
      durationMilliseconds: 15_000,
      crumbCount: 0,
      diagnostic: 'Query deadline exceeded.',
      startedAt: '2026-08-03T10:00:00Z',
      updatedAt: '2026-08-03T10:00:15Z',
    },
    {
      source: 'nomad',
      requestState: 'excluded',
      health: 'excluded',
      pass: 0,
      lookbackMinutes: 120,
      durationMilliseconds: 0,
      crumbCount: 0,
      diagnostic: 'Not selected for this Recipe.',
      startedAt: null,
      updatedAt: '2026-08-03T10:00:00Z',
    },
  ],
  earlyCrumbs: [
    {
      id: 'signal-1',
      source: 'grafana',
      occurredAt: '2026-08-03T09:59:00Z',
      severity: 'warning',
      summary: 'Database latency crossed the alert threshold.',
      confidence: 0.93,
    },
  ],
}
