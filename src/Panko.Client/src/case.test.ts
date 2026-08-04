import { describe, expect, it } from 'vitest'
import { caseInputTypeLabel } from './case'

describe('Case input vocabulary', () => {
  it('keeps the canonical Crumb discriminator', () => {
    expect(caseInputTypeLabel('crumb')).toBe('crumb')
  })
})
