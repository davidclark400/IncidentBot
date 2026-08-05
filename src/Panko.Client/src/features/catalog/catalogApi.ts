import type { OperationsCatalog } from './catalogModel'

export async function getOperationsCatalog(signal?: AbortSignal): Promise<OperationsCatalog> {
  const response = await fetch('/api/catalog', {
    cache: 'no-store',
    signal,
  })
  if (!response.ok) throw new Error('The operations catalog could not be loaded.')
  return response.json() as Promise<OperationsCatalog>
}
