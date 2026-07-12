import type { DemoAvailability } from '../../api-client/types.gen'

export async function getDemoAvailability(): Promise<DemoAvailability | null> {
  const response = await fetch('/api/demo', { cache: 'no-store' })
  return response.ok ? response.json() as Promise<DemoAvailability> : null
}

export async function resetDemo(): Promise<void> {
  const response = await fetch('/api/demo/reset', { method: 'POST' })
  if (!response.ok) throw new Error('Demo reset failed')
}
