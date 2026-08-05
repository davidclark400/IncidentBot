import { motion } from 'motion/react'
import type { ReactNode } from 'react'
import type { CodeReference } from '../caseFile'

export type BadgeTone = 'default' | 'success' | 'warning' | 'danger'

export function Badge({ children, tone = 'default' }: { children: ReactNode; tone?: BadgeTone }) {
  const tones: Record<BadgeTone, string> = {
    default: 'border-border bg-secondary text-secondary-foreground',
    success: 'border-emerald-500/25 bg-emerald-500/10 text-emerald-700 dark:text-emerald-300',
    warning: 'border-amber-500/25 bg-amber-500/10 text-amber-700 dark:text-amber-200',
    danger: 'border-rose-500/25 bg-rose-500/10 text-rose-700 dark:text-rose-200',
  }
  return <span className={`rounded-full border px-2.5 py-1 text-[11px] font-semibold uppercase tracking-wider ${tones[tone]}`}>{children}</span>
}

export function LiveItem({ children }: { children: ReactNode }) {
  return (
    <motion.div
      layout="position"
      initial={{ opacity: 0, y: 9 }}
      animate={{ opacity: 1, y: 0 }}
      exit={{ opacity: 0 }}
      transition={{
        layout: { type: 'spring', stiffness: 480, damping: 42, mass: 0.8 },
        opacity: { duration: 0.22, ease: 'easeOut' },
        y: { duration: 0.28, ease: [0.22, 1, 0.36, 1] },
      }}
    >
      {children}
    </motion.div>
  )
}

export function CodeReferenceLink({ reference }: { reference: CodeReference }) {
  return <a className="max-w-full break-all rounded-md border border-border bg-muted/50 px-2 py-1 font-mono text-[10px] text-foreground hover:border-foreground/30" href={reference.url} title={reference.excerpt} target="_blank" rel="noreferrer">{reference.path}:L{reference.startLine}-{reference.endLine} ↗</a>
}
