import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { CaseList } from './CaseList'
import { RecentPagerDutyIncidentList } from './RecentPagerDutyIncidentList'

describe('scoped Case activity empty states', () => {
  it('names the selected scope when there are no persisted Cases', () => {
    const markup = renderToStaticMarkup(
      <CaseList cases={[]} loading={false} scopeLabel="Payments platform" />,
    )

    expect(markup).toContain('No Cases for Payments platform')
  })

  it('names the selected scope when PagerDuty has no incidents', () => {
    const markup = renderToStaticMarkup(
      <RecentPagerDutyIncidentList
        incidents={[]}
        loading={false}
        onOpen={() => Promise.resolve()}
        scopeLabel="Payments production"
        openError={null}
        openingId={null}
      />,
    )

    expect(markup).toContain('No PagerDuty incidents for Payments production')
  })
})
