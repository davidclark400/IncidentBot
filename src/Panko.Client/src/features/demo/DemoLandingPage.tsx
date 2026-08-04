import { PageThemeControl } from '../../components/PageThemeControl'
import { PankoBrand } from '../../components/PankoBrand'
import type { ThemeControl } from '../../hooks/useTheme'
import { useDemoReplay } from './useDemoReplay'

export function DemoLandingPage({ theme }: { theme: ThemeControl }) {
  const { demo, starting, startDemo } = useDemoReplay()

  return (
    <main className="grid min-h-screen place-items-center px-4 py-20 sm:px-6">
      <PageThemeControl {...theme} />
      <section className="surface w-full max-w-2xl p-6 text-center sm:p-14">
        <PankoBrand className="justify-center" markClassName="size-10" />
        <h1 className="mt-4 text-3xl font-semibold tracking-tight text-foreground sm:text-4xl">Live operational context, without the scavenger hunt.</h1>
        <p className="mt-4 text-muted-foreground">Open a Case from its PagerDuty-triggered Slack message.</p>
        {demo && (
          <button
            className="mt-7 min-h-11 rounded-md bg-primary px-4 py-2.5 text-sm font-medium text-primary-foreground shadow-sm hover:opacity-90 disabled:opacity-60"
            disabled={starting}
            onClick={() => void startDemo()}
          >
            {starting ? 'Starting replay…' : 'Run live demo'}
          </button>
        )}
      </section>
    </main>
  )
}
