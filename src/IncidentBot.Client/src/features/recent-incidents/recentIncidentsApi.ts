import type { IncidentTriggerResult, ProblemDetails, RecentPagerDutyIncidents } from '../../api-client/types.gen'

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

export async function triggerPagerDutyIncident(id: string): Promise<IncidentTriggerResult> {
  const response = await fetch(`/api/pagerduty/incidents/${encodeURIComponent(id)}/trigger`, {
    method: 'POST',
  })
  if (!response.ok) throw new Error(await responseMessage(response, 'The investigation could not be started.'))
  return response.json() as Promise<IncidentTriggerResult>
}

async function responseMessage(response: Response, fallback: string) {
  try {
    const problem = await response.json() as ProblemDetails
    return problem.detail || problem.title || fallback
  } catch {
    return fallback
  }
}
