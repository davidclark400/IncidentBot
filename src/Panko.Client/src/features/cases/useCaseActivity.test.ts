import { describe, expect, it } from 'vitest'
import { parseRecentHours } from './useCaseActivity'

describe('recent Case query state', () => {
  it('accepts supported time frames and falls back safely', () => {
    expect(parseRecentHours('?hours=6')).toBe(6)
    expect(parseRecentHours('?hours=720')).toBe(720)
    expect(parseRecentHours('?hours=12')).toBe(24)
    expect(parseRecentHours('?hours=not-a-number')).toBe(24)
    expect(parseRecentHours('')).toBe(24)
  })
})
