import { PageThemeControl } from '../../components/PageThemeControl'
import type { ThemeControl } from '../../hooks/useTheme'

export function IncidentLoadingPage({ theme }: { theme: ThemeControl }) {
  return (
    <main className="grid min-h-screen place-items-center">
      <PageThemeControl {...theme} />
      <div className="text-center">
        <div className="mx-auto size-8 animate-spin rounded-full border-2 border-border border-t-foreground" />
        <p className="mt-4 text-sm text-muted-foreground">Opening live investigation…</p>
      </div>
    </main>
  )
}

export function IncidentErrorPage({ message, retry, theme }: { message: string; retry: () => Promise<void>; theme: ThemeControl }) {
  return (
    <main className="grid min-h-screen place-items-center px-4">
      <PageThemeControl {...theme} />
      <section className="surface max-w-lg p-6 text-center sm:p-8">
        <p className="text-lg font-semibold text-foreground">Investigation unavailable</p>
        <p className="mt-3 text-sm text-muted-foreground">{message}</p>
        <button className="mt-6 min-h-11 rounded-md bg-primary px-4 py-2 text-sm font-medium text-primary-foreground shadow-sm hover:opacity-90" onClick={() => void retry()}>Retry</button>
      </section>
    </main>
  )
}
