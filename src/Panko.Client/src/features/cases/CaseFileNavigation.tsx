import { useState, type ReactNode } from 'react'
import type { CaseFile } from '../../caseFile'

export function CaseFileNavigation({ caseFile }: { caseFile: CaseFile }) {
  const [activeTab, setActiveTab] = useState(() => window.location.hash.slice(1) || 'trail')

  return (
    <nav aria-label="Case File sections" className="sticky top-16 z-30 border-b border-border bg-background/95 px-4 py-2 backdrop-blur-xl lg:hidden">
      <div className="mx-auto flex max-w-7xl gap-2 overflow-x-auto [scrollbar-width:none] [&::-webkit-scrollbar]:hidden">
        <CaseFileTab href="#trail" active={activeTab === 'trail'} onSelect={setActiveTab}>Trail</CaseFileTab>
        <CaseFileTab href="#crumbs" active={activeTab === 'crumbs'} onSelect={setActiveTab}>Crumbs</CaseFileTab>
        <CaseFileTab href="#input-audit" active={activeTab === 'input-audit'} onSelect={setActiveTab}>Input audit</CaseFileTab>
        {(caseFile.causalMarkers?.length ?? 0) > 0 && <CaseFileTab href="#causal-sequence" active={activeTab === 'causal-sequence'} onSelect={setActiveTab}>Causal sequence</CaseFileTab>}
        {(caseFile.ai?.diagnoses?.length ?? 0) > 0 && <CaseFileTab href="#cited-diagnosis" active={activeTab === 'cited-diagnosis'} onSelect={setActiveTab}>Diagnosis</CaseFileTab>}
      </div>
    </nav>
  )
}

function CaseFileTab({ href, active, onSelect, children }: { href: `#${string}`; active: boolean; onSelect: (tab: string) => void; children: ReactNode }) {
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
