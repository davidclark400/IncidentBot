import { describe, expect, it } from 'vitest'
import {
  collectionHref,
  findCatalogLocation,
  parseCatalogRoute,
  resolveCatalogScope,
  scopeMatchesPagerDutyService,
  scopeMatchesRecipe,
  serviceHref,
  teamHref,
} from './catalogModel'
import { catalog } from './catalogTestFixture'

describe('operations catalog model', () => {
  it('parses the supported hierarchy and rejects malformed or unrelated paths', () => {
    expect(parseCatalogRoute('/')).toEqual({ kind: 'all' })
    expect(parseCatalogRoute('/teams/payments/')).toEqual({ kind: 'team', teamId: 'payments' })
    expect(parseCatalogRoute('/teams/payments/collections/checkout')).toEqual({
      kind: 'collection',
      teamId: 'payments',
      collectionId: 'checkout',
    })
    expect(parseCatalogRoute('/teams/payments/collections/checkout/services/payments-production')).toEqual({
      kind: 'service',
      teamId: 'payments',
      collectionId: 'checkout',
      recipeId: 'payments-production',
    })
    expect(parseCatalogRoute('/teams/team%20one/collections/system%20one/services/recipe%20one')).toEqual({
      kind: 'service',
      teamId: 'team one',
      collectionId: 'system one',
      recipeId: 'recipe one',
    })
    expect(parseCatalogRoute('/cases/not-a-catalog-route')).toEqual({ kind: 'unavailable' })
    expect(parseCatalogRoute('/teams/payments/services/payments-production')).toEqual({ kind: 'unavailable' })
    expect(parseCatalogRoute('/teams//collections/checkout')).toEqual({ kind: 'unavailable' })
    expect(parseCatalogRoute('/teams/%E0%A4%A')).toEqual({ kind: 'unavailable' })
  })

  it('resolves each level only beneath its catalog parent', () => {
    const team = resolveCatalogScope(catalog, { kind: 'team', teamId: 'payments' })
    const collection = resolveCatalogScope(catalog, {
      kind: 'collection',
      teamId: 'payments',
      collectionId: 'payments-platform',
    })
    const service = resolveCatalogScope(catalog, {
      kind: 'service',
      teamId: 'payments',
      collectionId: 'payments-platform',
      recipeId: 'payments-production',
    })

    expect(team?.recipeIds).toEqual(new Set(['payments-production', 'payments-staging']))
    expect(collection?.pagerDutyServiceIds).toEqual(new Set(['P123PAYMENTS', 'P456PAYMENTS']))
    expect(service).toMatchObject({
      title: 'Payments production',
      service: { recipeId: 'payments-production', pagerDutyServiceId: 'P123PAYMENTS' },
    })
    expect(resolveCatalogScope(catalog, {
      kind: 'collection',
      teamId: 'platform',
      collectionId: 'payments-platform',
    })).toBeNull()
    expect(resolveCatalogScope(catalog, {
      kind: 'service',
      teamId: 'payments',
      collectionId: 'payments-platform',
      recipeId: 'search-production',
    })).toBeNull()
  })

  it('matches activity through the catalog index while all-operations remains inclusive', () => {
    const all = resolveCatalogScope(catalog, { kind: 'all' })!
    const payments = resolveCatalogScope(catalog, { kind: 'team', teamId: 'payments' })!
    const production = resolveCatalogScope(catalog, {
      kind: 'service',
      teamId: 'payments',
      collectionId: 'payments-platform',
      recipeId: 'payments-production',
    })!

    expect(scopeMatchesRecipe(all, 'historical-recipe')).toBe(true)
    expect(scopeMatchesRecipe(payments, 'payments-staging')).toBe(true)
    expect(scopeMatchesRecipe(payments, 'search-production')).toBe(false)
    expect(scopeMatchesPagerDutyService(production, 'P123PAYMENTS')).toBe(true)
    expect(scopeMatchesPagerDutyService(production, 'P456PAYMENTS')).toBe(false)
  })

  it('finds Case breadcrumbs by recipe first and PagerDuty service as a fallback', () => {
    expect(findCatalogLocation(catalog, 'payments-production')).toMatchObject({
      team: { id: 'payments' },
      collection: { id: 'payments-platform' },
      service: { recipeId: 'payments-production' },
    })
    expect(findCatalogLocation(catalog, 'removed-recipe', 'PSEARCH')).toMatchObject({
      team: { id: 'platform' },
      collection: { id: 'search-platform' },
      service: { recipeId: 'search-production' },
    })
    expect(findCatalogLocation(catalog, 'missing', 'missing')).toBeNull()
    expect(findCatalogLocation({
      teams: [{
        id: 'payments',
        serviceCollections: [{
          id: 'payments-platform',
          services: [
            { recipeId: 'payments-api', pagerDutyServiceId: 'PSHARED' },
            { recipeId: 'payments-worker', pagerDutyServiceId: 'PSHARED' },
          ],
        }],
      }],
    }, 'removed-recipe', 'PSHARED')).toBeNull()
  })

  it('builds encoded, nested browse links', () => {
    expect(teamHref('team one')).toBe('/teams/team%20one')
    expect(collectionHref('team one', 'system one')).toBe('/teams/team%20one/collections/system%20one')
    expect(serviceHref('team one', 'system one', 'recipe one')).toBe('/teams/team%20one/collections/system%20one/services/recipe%20one')
  })
})
