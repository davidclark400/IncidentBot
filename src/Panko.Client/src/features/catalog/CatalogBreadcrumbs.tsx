import type { ObservedServiceCatalogItem, ServiceCollectionCatalogItem, TeamCatalogItem } from './catalogModel'
import { collectionHref, humanizeCatalogId, serviceHref, teamHref } from './catalogModel'

type BreadcrumbLevel = 'all' | 'team' | 'collection' | 'service' | 'case'

type CatalogBreadcrumbsProps = {
  team?: TeamCatalogItem | null
  collection?: ServiceCollectionCatalogItem | null
  service?: ObservedServiceCatalogItem | null
  current: BreadcrumbLevel
}

export function CatalogBreadcrumbs({ team, collection, service, current }: CatalogBreadcrumbsProps) {
  return (
    <nav aria-label="Breadcrumb" className="mb-5 overflow-x-auto text-xs text-muted-foreground [scrollbar-width:none] [&::-webkit-scrollbar]:hidden">
      <ol className="flex min-w-max items-center gap-2">
        <li>
          {current === 'all'
            ? <span aria-current="page" className="font-semibold text-foreground">All operations</span>
            : <a href="/" className="font-medium hover:text-foreground">All operations</a>}
        </li>
        {team && (
          <>
            <Separator />
            <li>
              {current === 'team'
                ? <span aria-current="page" className="font-semibold text-foreground">{humanizeCatalogId(team.id)}</span>
                : <a href={teamHref(team.id)} className="font-medium hover:text-foreground">{humanizeCatalogId(team.id)}</a>}
            </li>
          </>
        )}
        {team && collection && (
          <>
            <Separator />
            <li>
              {current === 'collection'
                ? <span aria-current="page" className="font-semibold text-foreground">{humanizeCatalogId(collection.id)}</span>
                : <a href={collectionHref(team.id, collection.id)} className="font-medium hover:text-foreground">{humanizeCatalogId(collection.id)}</a>}
            </li>
          </>
        )}
        {team && collection && service && (
          <>
            <Separator />
            <li>
              {current === 'service'
                ? <span aria-current="page" className="font-semibold text-foreground">{humanizeCatalogId(service.recipeId)}</span>
                : <a href={serviceHref(team.id, collection.id, service.recipeId)} className="font-medium hover:text-foreground">{humanizeCatalogId(service.recipeId)}</a>}
            </li>
          </>
        )}
        {current === 'case' && (
          <>
            <Separator />
            <li><span aria-current="page" className="font-semibold text-foreground">Case</span></li>
          </>
        )}
      </ol>
    </nav>
  )
}

function Separator() {
  return <li aria-hidden="true" className="text-border">/</li>
}
