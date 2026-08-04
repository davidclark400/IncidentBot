export type CaseRoute = Readonly<{
  caseId: string
}>

const casePattern = /^\/cases\/([0-9a-f-]+)\/?$/i

export function parseCaseRoute(pathname: string): CaseRoute | null {
  const match = pathname.match(casePattern)
  return match ? { caseId: match[1] } : null
}

export function resolveCaseRoute(
  location: Pick<Location, 'pathname'> = window.location,
) {
  const route = parseCaseRoute(location.pathname)
  return route?.caseId ?? null
}
