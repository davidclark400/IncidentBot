import type {
  CaseTriggerResult,
  ProblemDetails,
  RecentCases,
  RecentPagerDutyIncidents,
} from '../../api-client/types.gen'

export async function getRecentCases(signal?: AbortSignal): Promise<RecentCases> {
  const response = await fetch('/api/cases', {
    cache: 'no-store',
    signal,
  })
  if (!response.ok) throw new Error(await responseMessage(response, 'Recent Cases could not be loaded.'))
  return response.json() as Promise<RecentCases>
}

export async function getRecentPagerDutyIncidents(
  hours: number,
  signal?: AbortSignal,
): Promise<RecentPagerDutyIncidents> {
  const until = new Date()
  const since = new Date(until.getTime() - hours * 60 * 60 * 1000)
  const query = new URLSearchParams({ since: since.toISOString(), until: until.toISOString() })
  const response = await fetch(`/api/pagerduty/incidents?${query}`, {
    cache: 'no-store',
    signal,
  })
  if (!response.ok) throw new Error(await responseMessage(response, 'PagerDuty incidents could not be loaded.'))
  return response.json() as Promise<RecentPagerDutyIncidents>
}

export async function openPagerDutyCase(id: string): Promise<CaseTriggerResult> {
  const response = await fetch(`/api/pagerduty/incidents/${encodeURIComponent(id)}/trigger`, {
    method: 'POST',
  })
  if (!response.ok) throw new Error(await responseMessage(response, 'The Case could not be opened.'))
  return response.json() as Promise<CaseTriggerResult>
}

async function responseMessage(response: Response, fallback: string) {
  try {
    const details = await response.json() as ProblemDetails
    return details.detail || details.title || fallback
  } catch {
    return fallback
  }
}
