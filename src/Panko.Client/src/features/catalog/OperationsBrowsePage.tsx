import type { ReactNode } from 'react'
import { PankoBrand } from '../../components/PankoBrand'
import { ThemeToggle } from '../../components/ThemeToggle'
import type { ThemeControl } from '../../hooks/useTheme'
import { CaseActivity } from '../cases/CaseActivity'
import { CatalogBreadcrumbs } from './CatalogBreadcrumbs'
import { CatalogNavigation } from './CatalogNavigation'
import type { CatalogRoute, CatalogScope, OperationsCatalog } from './catalogModel'
import { collectionHref, humanizeCatalogId, resolveCatalogScope, serviceHref, teamHref } from './catalogModel'
import { useOperationsCatalog } from './useOperationsCatalog'

type BrowseRoute = Exclude<CatalogRoute, { kind: 'unavailable' }>

export function OperationsBrowsePage({ route, theme }: { route: BrowseRoute; theme: ThemeControl }) {
  const { catalog, error, loading, refresh } = useOperationsCatalog()

  return (
    <CatalogPageShell theme={theme}>
      {loading && !catalog
        ? <CatalogLoadingState />
        : error && !catalog
          ? <CatalogErrorState message={error} retry={refresh} />
          : !catalog || catalog.teams.length === 0
            ? <CatalogEmptyState />
            : <ResolvedCatalogPage catalog={catalog} route={route} />}
    </CatalogPageShell>
  )
}

export function CatalogUnavailablePage({ theme }: { theme: ThemeControl }) {
  return <CatalogPageShell theme={theme}><CatalogUnavailableState /></CatalogPageShell>
}

function ResolvedCatalogPage({ catalog, route }: { catalog: OperationsCatalog; route: BrowseRoute }) {
  const scope = resolveCatalogScope(catalog, route)
  if (!scope) return <CatalogUnavailableState />

  return (
    <div className="grid gap-6 lg:grid-cols-[16rem_minmax(0,1fr)] lg:gap-8">
      <CatalogNavigation catalog={catalog} scope={scope} />
      <div className="min-w-0">
        <CatalogBreadcrumbs
          team={scope.team}
          collection={scope.collection}
          service={scope.service}
          current={scope.route.kind}
        />
        <header className="max-w-3xl">
          <p className="eyebrow">{scope.eyebrow}</p>
          <h1 className="mt-3 text-3xl font-semibold tracking-tight text-foreground sm:text-4xl">{scope.title}</h1>
          <p className="mt-3 text-sm leading-6 text-muted-foreground sm:text-base">{scope.description}</p>
        </header>
        <CatalogChildren catalog={catalog} scope={scope} />
        <CaseActivity scope={scope} />
      </div>
    </div>
  )
}

export function CatalogChildren({ catalog, scope }: { catalog: OperationsCatalog; scope: CatalogScope }) {
  if (scope.route.kind === 'all') {
    return (
      <CatalogSection title="Teams">
        <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
          {scope.recipeIds.size === 0
            ? <InlineEmpty message="No observed services are configured." />
            : catalog.teams.map((team) => {
                const serviceCount = team.serviceCollections.reduce((total, collection) => total + collection.services.length, 0)
                return (
                  <CatalogCard
                    key={team.id}
                    href={teamHref(team.id)}
                    eyebrow="Team"
                    title={humanizeCatalogId(team.id)}
                    detail={`${team.serviceCollections.length} collection${team.serviceCollections.length === 1 ? '' : 's'} · ${serviceCount} observed service${serviceCount === 1 ? '' : 's'}`}
                  />
                )
              })}
        </div>
      </CatalogSection>
    )
  }

  if (scope.route.kind === 'team' && scope.team) {
    return (
      <CatalogSection title="Service collections">
        <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
          {scope.team.serviceCollections.map((collection) => (
            <CatalogCard
              key={collection.id}
              href={collectionHref(scope.team!.id, collection.id)}
              eyebrow="Service collection"
              title={humanizeCatalogId(collection.id)}
              detail={`${collection.services.length} observed service${collection.services.length === 1 ? '' : 's'}`}
            />
          ))}
        </div>
      </CatalogSection>
    )
  }

  if (scope.route.kind === 'collection' && scope.team && scope.collection) {
    return (
      <CatalogSection title="Observed services">
        <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
          {scope.collection.services.map((service) => (
            <CatalogCard
              key={service.recipeId}
              href={serviceHref(scope.team!.id, scope.collection!.id, service.recipeId)}
              eyebrow="Recipe"
              title={humanizeCatalogId(service.recipeId)}
              detail={`PagerDuty ${service.pagerDutyServiceId}`}
            />
          ))}
        </div>
      </CatalogSection>
    )
  }

  return (
    <CatalogSection title="Service details">
      <dl className="surface grid gap-px overflow-hidden bg-border sm:grid-cols-2 xl:grid-cols-4">
        <Detail label="Team" value={humanizeCatalogId(scope.team?.id ?? '')} />
        <Detail label="Collection" value={humanizeCatalogId(scope.collection?.id ?? '')} />
        <Detail label="Recipe" value={scope.service?.recipeId ?? ''} />
        <Detail label="PagerDuty service" value={scope.service?.pagerDutyServiceId ?? ''} />
      </dl>
    </CatalogSection>
  )
}

export function CatalogLoadingState() {
  return (
    <div aria-label="Loading operations catalog" className="grid animate-pulse gap-6 lg:grid-cols-[16rem_minmax(0,1fr)] lg:gap-8">
      <div className="hidden h-80 rounded-lg bg-muted lg:block" />
      <div>
        <div className="h-3 w-32 rounded bg-muted" />
        <div className="mt-5 h-10 max-w-md rounded bg-muted" />
        <div className="mt-4 h-4 max-w-xl rounded bg-muted" />
        <div className="mt-10 grid gap-3 sm:grid-cols-3">
          {[0, 1, 2].map((item) => <div key={item} className="h-32 rounded-lg bg-muted" />)}
        </div>
      </div>
    </div>
  )
}

export function CatalogErrorState({ message, retry }: { message: string; retry: () => void }) {
  return (
    <CenteredState title="Operations could not be loaded" message={message}>
      <button type="button" className="mt-6 min-h-11 rounded-md bg-primary px-4 py-2 text-sm font-semibold text-primary-foreground shadow-sm hover:opacity-90" onClick={retry}>Try again</button>
    </CenteredState>
  )
}

export function CatalogEmptyState() {
  return (
    <CenteredState
      title="No teams are available"
      message="Add a Recipe for local testing, or ask for access to a configured team."
    />
  )
}

export function CatalogUnavailableState() {
  return (
    <CenteredState
      title="Operations page unavailable"
      message="This team, service collection, or observed service does not exist or is not available to you."
    >
      <a href="/" className="mt-6 inline-flex min-h-11 items-center rounded-md bg-primary px-4 py-2 text-sm font-semibold text-primary-foreground shadow-sm hover:opacity-90">Back to all operations</a>
    </CenteredState>
  )
}

function CatalogPageShell({ theme, children }: { theme: ThemeControl; children: ReactNode }) {
  return (
    <div className="min-h-screen bg-background text-foreground">
      <header className="sticky top-0 z-40 border-b border-border bg-background/90 backdrop-blur-xl">
        <div className="mx-auto flex min-h-16 max-w-7xl items-center justify-between px-4 py-2.5 sm:px-5 lg:px-8">
          <PankoBrand href="/" subtitle="Operations catalog" />
          <ThemeToggle {...theme} />
        </div>
      </header>
      <main className="mx-auto max-w-7xl px-4 py-8 sm:px-5 sm:py-10 lg:px-8">{children}</main>
    </div>
  )
}

function CatalogSection({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="mt-8" aria-labelledby={`catalog-${title.toLowerCase().replaceAll(' ', '-')}`}>
      <h2 id={`catalog-${title.toLowerCase().replaceAll(' ', '-')}`} className="mb-3 text-sm font-semibold text-foreground">{title}</h2>
      {children}
    </section>
  )
}

function CatalogCard({ href, eyebrow, title, detail }: { href: string; eyebrow: string; title: string; detail: string }) {
  return (
    <a href={href} className="surface group block min-h-32 p-4 hover:border-ring hover:bg-accent/40">
      <p className="text-[10px] font-semibold uppercase tracking-wider text-muted-foreground">{eyebrow}</p>
      <div className="mt-3 flex items-start justify-between gap-3">
        <h3 className="font-semibold text-foreground group-hover:underline">{title}</h3>
        <ArrowIcon />
      </div>
      <p className="mt-2 text-xs text-muted-foreground">{detail}</p>
    </a>
  )
}

function Detail({ label, value }: { label: string; value: string }) {
  return <div className="bg-card p-4"><dt className="text-[10px] font-semibold uppercase tracking-wider text-muted-foreground">{label}</dt><dd className="mt-2 break-words text-sm font-medium text-foreground">{value}</dd></div>
}

function InlineEmpty({ message }: { message: string }) {
  return <p className="surface p-5 text-sm text-muted-foreground sm:col-span-2 xl:col-span-3">{message}</p>
}

function CenteredState({ title, message, children }: { title: string; message: string; children?: ReactNode }) {
  return (
    <section className="mx-auto grid min-h-[65vh] max-w-xl place-items-center text-center">
      <div className="surface w-full p-7 sm:p-10">
        <div className="mx-auto grid size-12 place-items-center rounded-full border border-border bg-muted/50 text-muted-foreground"><HierarchyIcon /></div>
        <h1 className="mt-5 text-xl font-semibold text-foreground">{title}</h1>
        <p className="mt-2 text-sm leading-6 text-muted-foreground">{message}</p>
        {children}
      </div>
    </section>
  )
}

function ArrowIcon() {
  return <svg aria-hidden="true" viewBox="0 0 24 24" className="mt-0.5 size-4 shrink-0 text-muted-foreground group-hover:text-foreground" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="m9 18 6-6-6-6" /></svg>
}

function HierarchyIcon() {
  return <svg aria-hidden="true" viewBox="0 0 24 24" className="size-5" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><path d="M12 3v6M6 21v-5h12v5M6 16v-3h12v3" /><circle cx="12" cy="3" r="1" /><circle cx="6" cy="21" r="1" /><circle cx="18" cy="21" r="1" /></svg>
}
