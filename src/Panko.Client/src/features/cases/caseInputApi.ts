import type { ProblemDetails } from '../../api-client/types.gen'
import type { PageOfCaseInput } from '../../case'

export async function getCaseInputs(
  caseId: string,
  offset = 0,
  limit = 100,
  signal?: AbortSignal,
): Promise<PageOfCaseInput> {
  const query = new URLSearchParams({ offset: String(offset), limit: String(limit) })
  const response = await fetch(`/api/cases/${encodeURIComponent(caseId)}/inputs?${query}`, {
    cache: 'no-store',
    signal,
  })
  if (!response.ok) throw new Error(await responseMessage(response, 'Input history could not be loaded.'))
  return response.json() as Promise<PageOfCaseInput>
}

async function responseMessage(response: Response, fallback: string) {
  try {
    const details = await response.json() as ProblemDetails
    return details.detail || details.title || fallback
  } catch {
    return fallback
  }
}
