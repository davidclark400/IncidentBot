import type {
  ObservedServiceCatalogItem,
  OperationsCatalog,
  ServiceCollectionCatalogItem,
  TeamCatalogItem,
} from '../../api-client/types.gen'

export type {
  ObservedServiceCatalogItem,
  OperationsCatalog,
  ServiceCollectionCatalogItem,
  TeamCatalogItem,
}

export type CatalogRoute =
  | { kind: 'all' }
  | { kind: 'team'; teamId: string }
  | { kind: 'collection'; teamId: string; collectionId: string }
  | { kind: 'service'; teamId: string; collectionId: string; recipeId: string }
  | { kind: 'unavailable' }

export type CatalogScope = {
  route: Exclude<CatalogRoute, { kind: 'unavailable' }>
  team: TeamCatalogItem | null
  collection: ServiceCollectionCatalogItem | null
  service: ObservedServiceCatalogItem | null
  recipeIds: ReadonlySet<string>
  pagerDutyServiceIds: ReadonlySet<string>
  eyebrow: string
  title: string
  description: string
  label: string
}

export type CatalogLocation = {
  team: TeamCatalogItem
  collection: ServiceCollectionCatalogItem
  service: ObservedServiceCatalogItem
}

export function parseCatalogRoute(pathname: string): CatalogRoute {
  if (pathname === '/' || pathname === '') return { kind: 'all' }
  if (pathname.includes('//')) return { kind: 'unavailable' }
  const segments = pathname.replace(/^\//, '').replace(/\/$/, '').split('/')
  if (segments[0] !== 'teams') return { kind: 'unavailable' }
  const decoded = segments.slice(1).map(decodeSegment)
  if (decoded.some((segment) => segment === null)) return { kind: 'unavailable' }
  const [teamId, collectionLabel, collectionId, serviceLabel, recipeId] = decoded as string[]
  if (segments.length === 2 && teamId) return { kind: 'team', teamId }
  if (segments.length === 4 && teamId && collectionLabel === 'collections' && collectionId) {
    return { kind: 'collection', teamId, collectionId }
  }
  if (segments.length === 6 && teamId && collectionLabel === 'collections' && collectionId && serviceLabel === 'services' && recipeId) {
    return { kind: 'service', teamId, collectionId, recipeId }
  }
  return { kind: 'unavailable' }
}

export function resolveCatalogScope(catalog: OperationsCatalog, route: CatalogRoute): CatalogScope | null {
  if (route.kind === 'unavailable') return null
  if (route.kind === 'all') {
    const services = catalog.teams.flatMap((team) => team.serviceCollections.flatMap((collection) => collection.services))
    return scope(route, null, null, null, services, 'Operations', 'All operations', 'Browse authorized teams, service collections, and observed services.', 'all authorized teams')
  }

  const team = catalog.teams.find((candidate) => candidate.id === route.teamId)
  if (!team) return null
  if (route.kind === 'team') {
    const services = team.serviceCollections.flatMap((collection) => collection.services)
    return scope(route, team, null, null, services, 'Team', humanizeCatalogId(team.id), `${team.serviceCollections.length} service collection${team.serviceCollections.length === 1 ? '' : 's'} owned by this team.`, humanizeCatalogId(team.id))
  }

  const collection = team.serviceCollections.find((candidate) => candidate.id === route.collectionId)
  if (!collection) return null
  if (route.kind === 'collection') {
    return scope(route, team, collection, null, collection.services, 'Service collection', humanizeCatalogId(collection.id), `${collection.services.length} observed service${collection.services.length === 1 ? '' : 's'} operated together by ${humanizeCatalogId(team.id)}.`, humanizeCatalogId(collection.id))
  }

  const service = collection.services.find((candidate) => candidate.recipeId === route.recipeId)
  if (!service) return null
  return scope(route, team, collection, service, [service], 'Observed service', humanizeCatalogId(service.recipeId), `Recipe ${service.recipeId} · PagerDuty service ${service.pagerDutyServiceId}`, humanizeCatalogId(service.recipeId))
}

export function findCatalogLocation(
  catalog: OperationsCatalog,
  recipeId: string,
  pagerDutyServiceId?: string,
): CatalogLocation | null {
  let serviceIdFallback: CatalogLocation | null = null
  let serviceIdIsAmbiguous = false
  for (const team of catalog.teams) {
    for (const collection of team.serviceCollections) {
      for (const service of collection.services) {
        const location = { team, collection, service }
        if (service.recipeId === recipeId) return location
        if (pagerDutyServiceId && service.pagerDutyServiceId === pagerDutyServiceId) {
          if (serviceIdFallback) serviceIdIsAmbiguous = true
          else serviceIdFallback = location
        }
      }
    }
  }
  return serviceIdIsAmbiguous ? null : serviceIdFallback
}

export function scopeMatchesRecipe(scope: CatalogScope, recipeId: string) {
  return scope.route.kind === 'all' || scope.recipeIds.has(recipeId)
}

export function scopeMatchesPagerDutyService(scope: CatalogScope, serviceId: string) {
  return scope.route.kind === 'all' || scope.pagerDutyServiceIds.has(serviceId)
}

export function teamHref(teamId: string) {
  return `/teams/${encodeURIComponent(teamId)}`
}

export function collectionHref(teamId: string, collectionId: string) {
  return `${teamHref(teamId)}/collections/${encodeURIComponent(collectionId)}`
}

export function serviceHref(teamId: string, collectionId: string, recipeId: string) {
  return `${collectionHref(teamId, collectionId)}/services/${encodeURIComponent(recipeId)}`
}

export function humanizeCatalogId(value: string) {
  const words = value.replace(/[-_]+/g, ' ').trim()
  if (!words) return value
  return words.charAt(0).toUpperCase() + words.slice(1)
}

function scope(
  route: CatalogScope['route'],
  team: TeamCatalogItem | null,
  collection: ServiceCollectionCatalogItem | null,
  service: ObservedServiceCatalogItem | null,
  services: ObservedServiceCatalogItem[],
  eyebrow: string,
  title: string,
  description: string,
  label: string,
): CatalogScope {
  return {
    route,
    team,
    collection,
    service,
    recipeIds: new Set(services.map((item) => item.recipeId)),
    pagerDutyServiceIds: new Set(services.map((item) => item.pagerDutyServiceId)),
    eyebrow,
    title,
    description,
    label,
  }
}

function decodeSegment(value: string): string | null {
  try {
    return decodeURIComponent(value)
  } catch {
    return null
  }
}
