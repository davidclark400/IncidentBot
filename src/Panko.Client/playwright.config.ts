import { defineConfig, devices } from '@playwright/test'

export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  retries: process.env.CI ? 2 : 0,
  reporter: process.env.CI ? 'github' : 'list',
  use: {
    baseURL: 'http://127.0.0.1:5074',
    trace: 'retain-on-failure',
  },
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
  ],
  webServer: {
    command: 'npm run build && ASPNETCORE_ENVIRONMENT=Development Demo__Enabled=true Demo__StepDelaySeconds=1 "$HOME/.dotnet/dotnet" run --project ../Panko.Api --urls http://127.0.0.1:5074 --no-launch-profile',
    url: 'http://127.0.0.1:5074/health/ready',
    reuseExistingServer: false,
    timeout: 120_000,
  },
})
