import { useState, type ReactNode } from 'react'
import type { Report } from '../../incidentReport'

export function ReportNavigation({ report }: { report: Report }) {
  const [activeTab, setActiveTab] = useState(() => window.location.hash.slice(1) || 'timeline')

  return (
    <nav aria-label="Report sections" className="sticky top-16 z-30 border-b border-border bg-background/95 px-4 py-2 backdrop-blur-xl lg:hidden">
      <div className="mx-auto flex max-w-7xl gap-2 overflow-x-auto [scrollbar-width:none] [&::-webkit-scrollbar]:hidden">
        <ReportTab href="#timeline" active={activeTab === 'timeline'} onSelect={setActiveTab}>Timeline</ReportTab>
        <ReportTab href="#evidence" active={activeTab === 'evidence'} onSelect={setActiveTab}>Evidence</ReportTab>
        {(report.causalEvents?.length ?? 0) > 0 && <ReportTab href="#causal-sequence" active={activeTab === 'causal-sequence'} onSelect={setActiveTab}>Causal sequence</ReportTab>}
        {(report.ai?.diagnoses?.length ?? 0) > 0 && <ReportTab href="#cited-diagnosis" active={activeTab === 'cited-diagnosis'} onSelect={setActiveTab}>Diagnosis</ReportTab>}
      </div>
    </nav>
  )
}

function ReportTab({ href, active, onSelect, children }: { href: `#${string}`; active: boolean; onSelect: (tab: string) => void; children: ReactNode }) {
  return (
    <a
      className={`min-h-10 shrink-0 rounded-full border px-4 py-2.5 text-xs font-semibold ${active ? 'border-primary bg-primary text-primary-foreground' : 'border-border bg-card text-foreground'}`}
      href={href}
      aria-current={active ? 'location' : undefined}
      onClick={() => onSelect(href.slice(1))}
    >
      {children}
    </a>
  )
}
