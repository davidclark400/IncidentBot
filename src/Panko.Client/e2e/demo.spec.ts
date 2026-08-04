import { expect, test } from '@playwright/test'

test('demo completes the first-run Case workflow', async ({ page }) => {
  await page.goto('/')

  await expect(page.getByRole('heading', { name: 'All operations' })).toBeVisible()
  await page.getByRole('link', { name: /Payments/ }).first().click()
  await expect(page).toHaveURL(/\/teams\/payments$/)
  await page.getByRole('link', { name: /Payments platform/ }).first().click()
  await expect(page).toHaveURL(/\/teams\/payments\/collections\/payments-platform$/)
  await page.getByRole('link', { name: /Payments production/ }).first().click()
  await expect(page).toHaveURL(/\/teams\/payments\/collections\/payments-platform\/services\/payments-production$/)
  await expect(page.getByRole('heading', { name: 'Recent Cases' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'PagerDuty incidents' })).toBeVisible()
  await expect(page.getByRole('alert')).toHaveCount(0)
  await expect(page.getByRole('region', { name: 'Persisted Cases' }).getByRole('link', { name: 'Payment authorisations timing out' })).toBeVisible()
  await expect(page.getByRole('region', { name: 'PagerDuty incidents' }).getByText('Payment authorisations timing out')).toBeVisible()
  await page.getByRole('button', { name: 'Open case' }).click()

  await expect(page).toHaveURL(/\/cases\/11111111-1111-1111-1111-111111111111$/)
  await expect(page.getByRole('heading', { name: 'Payment authorisations timing out' })).toBeVisible()
  await expect(page.getByRole('navigation', { name: 'Breadcrumb' }).getByRole('link', { name: 'Payments production' })).toBeVisible()
  await expect(page.getByText('PDEMO')).toBeVisible()
  await expect(page.getByText('Requested', { exact: true }).first()).toBeVisible()
  await expect(page.locator('[data-source-request][data-request-state="requested"]').first()).toBeVisible()
  await expect(page.getByText('PAYMENTS-CHECKOUT-4F19')).toBeVisible({ timeout: 15_000 })
  await expect(page.getByRole('heading', { name: '4 occurrences' })).toBeVisible()
  await expect(page.getByText('90% similarity match')).toBeVisible()
  await expect(page.getByText(/Matched on payment authorisation timeout/)).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Model assessment tied to collected Crumbs' })).toBeVisible({ timeout: 15_000 })
  await expect(page.getByText('AI synthesis: complete. The deterministic Case File remains canonical.')).toBeVisible()
  await expect(page.getByRole('region', { name: 'Durable canonical inputs' }).getByText('No durable Case inputs are available yet.')).toBeVisible()
  await expect(page.getByRole('alert')).toHaveCount(0)
  await expect(page.getByText('5/5')).toBeVisible()
  await expect(page.getByText('All source requests received')).toBeVisible()
  await expect(page.locator('[data-source-request]')).toHaveCount(0)
  await expect(page.getByRole('link', { name: /Handler\.cs:L43-44/ }).first()).toBeVisible()
})

test('unknown hierarchy path does not disclose catalog membership', async ({ page }) => {
  await page.goto('/teams/payments/collections/not-available')

  await expect(page.getByRole('heading', { name: 'Operations page unavailable' })).toBeVisible()
  await expect(page.getByText('does not exist or is not available to you')).toBeVisible()
  await expect(page.getByRole('link', { name: 'Back to all operations' })).toBeVisible()
})

test('unknown Case exposes a recoverable error state', async ({ page }) => {
  await page.goto('/cases/22222222-2222-2222-2222-222222222222')

  await expect(page.getByText('Case unavailable')).toBeVisible()
  await expect(page.getByText('This Case does not exist or has expired.')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Retry' })).toBeVisible()
})

test('mobile responders can reach the Trail without horizontal overflow', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await page.goto('/')
  await expect(page.getByRole('region', { name: 'PagerDuty incidents' }).getByText('Payment authorisations timing out')).toBeVisible()
  await page.getByRole('button', { name: 'Open case' }).click()

  await expect(page.getByRole('heading', { name: 'Payment authorisations timing out' })).toBeVisible()
  const sectionNav = page.getByRole('navigation', { name: 'Case File sections' })
  await expect(sectionNav).toBeVisible()
  await sectionNav.getByRole('link', { name: 'Trail', exact: true }).click()

  const trailHeading = page.getByRole('heading', { name: 'What changed' })
  await expect(trailHeading).toBeInViewport()
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true)
})
