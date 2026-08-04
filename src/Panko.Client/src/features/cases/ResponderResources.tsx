import type { Link } from '../../caseFile'

export function ResponderResources({ checks, links }: { checks: string[]; links: Link[] }) {
  if (checks.length === 0 && links.length === 0) return null

  return (
    <section className="mt-6 grid gap-6 lg:grid-cols-2">
      <article className="surface p-6">
        <p className="eyebrow">Responder checks</p>
        <ul className="mt-4 space-y-3 text-sm text-foreground">
          {checks.map((item) => <li key={item} className="flex gap-3"><span className="text-foreground">→</span>{item}</li>)}
        </ul>
      </article>
      <article className="surface p-6">
        <p className="eyebrow">Scoped source links</p>
        <div className="mt-4 flex flex-wrap gap-2">
          {links.map((link) => <a className="source-link" href={link.url} target="_blank" rel="noreferrer" key={link.url}>{link.label} ↗</a>)}
        </div>
      </article>
    </section>
  )
}
