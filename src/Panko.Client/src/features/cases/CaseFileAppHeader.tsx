import { AnimatePresence, motion } from 'motion/react'
import { useEffect, useRef, useState } from 'react'
import { PankoBrand } from '../../components/PankoBrand'
import { ThemeToggle } from '../../components/ThemeToggle'
import type { ThemeControl } from '../../hooks/useTheme'

export function CaseFileAppHeader({ connected, caseFileVersion, theme }: { connected: boolean; caseFileVersion: number; theme: ThemeControl }) {
  const hasFreshData = useFreshDataIndicator(caseFileVersion)

  return (
    <div className="sticky top-0 z-40 border-b border-border bg-background/90 backdrop-blur-xl">
      <div className="mx-auto flex min-h-16 max-w-7xl items-center justify-between px-4 py-2.5 sm:px-5 lg:px-8">
        <PankoBrand href="/" subtitle="Case File" />
        <div className="flex items-center gap-2 text-xs text-muted-foreground sm:gap-3">
          <ThemeToggle {...theme} />
          <span className="hidden h-4 w-px bg-border sm:block" />
          <span className={`status-dot ${connected ? 'bg-emerald-400' : 'bg-amber-400'}`} />
          <span className="hidden min-w-20 sm:inline" aria-live="polite">
            <AnimatePresence initial={false} mode="wait">
              <motion.span
                key={hasFreshData ? 'fresh' : 'connection'}
                initial={{ opacity: 0, y: 3 }}
                animate={{ opacity: 1, y: 0 }}
                exit={{ opacity: 0, y: -3 }}
                transition={{ duration: 0.18, ease: 'easeOut' }}
                className={hasFreshData ? 'inline-block font-medium text-emerald-600 dark:text-emerald-300' : 'inline-block'}
              >
                {hasFreshData ? 'New data' : connected ? 'Live updates' : 'Reconnecting'}
              </motion.span>
            </AnimatePresence>
          </span>
          <span className="hidden text-border sm:inline">•</span>
          <span className="hidden sm:inline">v{caseFileVersion}</span>
        </div>
      </div>
    </div>
  )
}

function useFreshDataIndicator(caseFileVersion: number) {
  const previousVersion = useRef(caseFileVersion)
  const [hasFreshData, setHasFreshData] = useState(false)

  useEffect(() => {
    if (caseFileVersion <= previousVersion.current) return
    previousVersion.current = caseFileVersion
    setHasFreshData(true)
    const timer = window.setTimeout(() => setHasFreshData(false), 1800)
    return () => window.clearTimeout(timer)
  }, [caseFileVersion])

  return hasFreshData
}
