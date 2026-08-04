import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { CatalogBreadcrumbs } from './CatalogBreadcrumbs'
import { CatalogNavigation } from './CatalogNavigation'
import {
  CatalogChildren,
  CatalogEmptyState,
  CatalogErrorState,
  CatalogLoadingState,
  CatalogUnavailableState,
} from './OperationsBrowsePage'
import { resolveCatalogScope } from './catalogModel'
import { catalog } from './catalogTestFixture'

describe('operations catalog components', () => {
  it('renders the selected hierarchy and nested navigation links', () => {
    const scope = resolveCatalogScope(catalog, {
      kind: 'service',
      teamId: 'payments',
      collectionId: 'payments-platform',
      recipeId: 'payments-production',
    })!
    const markup = renderToStaticMarkup(<CatalogNavigation catalog={catalog} scope={scope} />)

    expect(markup).toContain('Operations hierarchy')
    expect(markup).toContain('href="/teams/payments"')
    expect(markup).toContain('href="/teams/payments/collections/payments-platform"')
    expect(markup).toContain('href="/teams/payments/collections/payments-platform/services/payments-production"')
    expect(markup).toContain('aria-current="page"')
  })

  it('renders team, collection, and observed-service cards for each browse level', () => {
    const all = resolveCatalogScope(catalog, { kind: 'all' })!
    const team = resolveCatalogScope(catalog, { kind: 'team', teamId: 'payments' })!
    const collection = resolveCatalogScope(catalog, {
      kind: 'collection',
      teamId: 'payments',
      collectionId: 'payments-platform',
    })!
    const service = resolveCatalogScope(catalog, {
      kind: 'service',
      teamId: 'payments',
      collectionId: 'payments-platform',
      recipeId: 'payments-production',
    })!

    expect(renderToStaticMarkup(<CatalogChildren catalog={catalog} scope={all} />)).toContain('2 observed services')
    expect(renderToStaticMarkup(<CatalogChildren catalog={catalog} scope={team} />)).toContain('Payments platform')
    expect(renderToStaticMarkup(<CatalogChildren catalog={catalog} scope={collection} />)).toContain('PagerDuty P123PAYMENTS')
    const serviceMarkup = renderToStaticMarkup(<CatalogChildren catalog={catalog} scope={service} />)
    expect(serviceMarkup).toContain('Service details')
    expect(serviceMarkup).toContain('P123PAYMENTS')
  })

  it('renders a Case as a leaf after its team, collection, and service', () => {
    const scope = resolveCatalogScope(catalog, {
      kind: 'service',
      teamId: 'payments',
      collectionId: 'payments-platform',
      recipeId: 'payments-production',
    })!
    const markup = renderToStaticMarkup(
      <CatalogBreadcrumbs
        team={scope.team}
        collection={scope.collection}
        service={scope.service}
        current="case"
      />,
    )

    expect(markup).toContain('All operations')
    expect(markup).toContain('Payments platform')
    expect(markup).toContain('Payments production')
    expect(markup).toContain('aria-current="page"')
    expect(markup).toContain('>Case</span>')
  })

  it('provides distinct loading, error, empty, and inaccessible states', () => {
    expect(renderToStaticMarkup(<CatalogLoadingState />)).toContain('Loading operations catalog')
    expect(renderToStaticMarkup(<CatalogErrorState message="Network unavailable" retry={() => undefined} />)).toContain('Network unavailable')
    expect(renderToStaticMarkup(<CatalogEmptyState />)).toContain('No teams are available')
    expect(renderToStaticMarkup(<CatalogUnavailableState />)).toContain('does not exist or is not available to you')
  })
})
