import { describe, expect, it } from 'vitest'
import { parseCaseRoute, resolveCaseRoute } from './caseRoute'

describe('Case route', () => {
  it('parses the canonical route', () => {
    expect(parseCaseRoute('/cases/11111111-1111-1111-1111-111111111111')).toEqual({
      caseId: '11111111-1111-1111-1111-111111111111',
    })
  })

  it('rejects non-Case routes', () => {
    expect(resolveCaseRoute({ pathname: '/not-a-case' })).toBeNull()
  })
})
