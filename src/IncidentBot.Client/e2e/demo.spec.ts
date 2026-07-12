import { expect, test } from '@playwright/test'

test('demo completes the first-run investigation workflow', async ({ page }) => {
  await page.goto('/')

  await expect(page.getByRole('heading', { name: /Live operational context/ })).toBeVisible()
  await page.getByRole('button', { name: 'Run live demo' }).click()

  await expect(page).toHaveURL(/\/incidents\/11111111-1111-1111-1111-111111111111$/)
  await expect(page.getByRole('heading', { name: 'Payment authorisations timing out' })).toBeVisible()
  await expect(page.getByText('PDEMO')).toBeVisible()
  await expect(page.getByText('PAYMENTS-CHECKOUT-4F19')).toBeVisible({ timeout: 15_000 })
  await expect(page.getByRole('heading', { name: '4 occurrences' })).toBeVisible()
  await expect(page.getByText('90% similarity match')).toBeVisible()
  await expect(page.getByText(/Matched on payment authorisation timeout/)).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Model assessment tied to collected evidence' })).toBeVisible({ timeout: 15_000 })
  await expect(page.getByText('AI synthesis: complete. Deterministic evidence remains canonical.')).toBeVisible()
  await expect(page.getByText('5/5')).toBeVisible()
  await expect(page.getByRole('link', { name: /Handler\.cs:L43-44/ }).first()).toBeVisible()
})

test('unknown incident exposes a recoverable error state', async ({ page }) => {
  await page.goto('/incidents/22222222-2222-2222-2222-222222222222')

  await expect(page.getByText('Investigation unavailable')).toBeVisible()
  await expect(page.getByText('This investigation does not exist or has expired.')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Retry' })).toBeVisible()
})

test('mobile responders can reach the timeline without horizontal overflow', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await page.goto('/')
  await page.getByRole('button', { name: 'Run live demo' }).click()

  await expect(page.getByRole('heading', { name: 'Payment authorisations timing out' })).toBeVisible()
  const sectionNav = page.getByRole('navigation', { name: 'Report sections' })
  await expect(sectionNav).toBeVisible()
  await sectionNav.getByRole('link', { name: 'Timeline', exact: true }).click()

  const timelineHeading = page.getByRole('heading', { name: 'What changed' })
  await expect(timelineHeading).toBeInViewport()
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true)
})
