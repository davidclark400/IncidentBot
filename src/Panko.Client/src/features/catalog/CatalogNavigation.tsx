import type { CatalogScope, OperationsCatalog } from './catalogModel'
import { collectionHref, humanizeCatalogId, serviceHref, teamHref } from './catalogModel'

type CatalogNavigationProps = {
  catalog: OperationsCatalog
  scope: CatalogScope
}

export function CatalogNavigation({ catalog, scope }: CatalogNavigationProps) {
  return (
    <>
      <aside className="hidden lg:block">
        <div className="sticky top-24 rounded-lg border border-border bg-card p-3 shadow-sm">
          <p className="px-2 pb-2 text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">Browse</p>
          <CatalogTree catalog={catalog} scope={scope} label="Operations hierarchy" />
        </div>
      </aside>
      <details className="rounded-lg border border-border bg-card shadow-sm lg:hidden">
        <summary className="cursor-pointer px-4 py-3 text-sm font-semibold text-foreground">Browse teams and services</summary>
        <div className="border-t border-border p-3">
          <CatalogTree catalog={catalog} scope={scope} label="Mobile operations hierarchy" />
        </div>
      </details>
    </>
  )
}

function CatalogTree({ catalog, scope, label }: CatalogNavigationProps & { label: string }) {
  return (
    <nav aria-label={label}>
      <ul className="space-y-1">
        <li><TreeLink href="/" active={scope.route.kind === 'all'} label="All operations" /></li>
        {catalog.teams.map((team) => {
          const teamSelected = scope.team?.id === team.id
          return (
            <li key={team.id}>
              <TreeLink
                href={teamHref(team.id)}
                active={scope.route.kind === 'team' && teamSelected}
                label={humanizeCatalogId(team.id)}
                count={team.serviceCollections.length}
              />
              {teamSelected && (
                <ul className="ml-3 mt-1 space-y-1 border-l border-border pl-2">
                  {team.serviceCollections.map((collection) => {
                    const collectionSelected = scope.collection?.id === collection.id
                    return (
                      <li key={collection.id}>
                        <TreeLink
                          href={collectionHref(team.id, collection.id)}
                          active={scope.route.kind === 'collection' && collectionSelected}
                          label={humanizeCatalogId(collection.id)}
                          count={collection.services.length}
                        />
                        {collectionSelected && (
                          <ul className="ml-3 mt-1 space-y-1 border-l border-border pl-2">
                            {collection.services.map((service) => (
                              <li key={service.recipeId}>
                                <TreeLink
                                  href={serviceHref(team.id, collection.id, service.recipeId)}
                                  active={scope.route.kind === 'service' && scope.service?.recipeId === service.recipeId}
                                  label={humanizeCatalogId(service.recipeId)}
                                />
                              </li>
                            ))}
                          </ul>
                        )}
                      </li>
                    )
                  })}
                </ul>
              )}
            </li>
          )
        })}
      </ul>
    </nav>
  )
}

function TreeLink({ href, active, label, count }: { href: string; active: boolean; label: string; count?: number }) {
  return (
    <a
      href={href}
      aria-current={active ? 'page' : undefined}
      className={`flex min-h-9 items-center justify-between gap-2 rounded-md px-2.5 py-2 text-sm ${active ? 'bg-primary font-semibold text-primary-foreground' : 'text-foreground hover:bg-accent'}`}
    >
      <span className="truncate">{label}</span>
      {count !== undefined && <span className={`text-[10px] ${active ? 'text-primary-foreground/75' : 'text-muted-foreground'}`}>{count}</span>}
    </a>
  )
}
