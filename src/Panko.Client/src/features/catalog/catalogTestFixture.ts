import type { OperationsCatalog } from './catalogModel'

export const catalog: OperationsCatalog = {
  teams: [
    {
      id: 'payments',
      serviceCollections: [
        {
          id: 'payments-platform',
          services: [
            { recipeId: 'payments-production', pagerDutyServiceId: 'P123PAYMENTS' },
            { recipeId: 'payments-staging', pagerDutyServiceId: 'P456PAYMENTS' },
          ],
        },
      ],
    },
    {
      id: 'platform',
      serviceCollections: [
        {
          id: 'search-platform',
          services: [{ recipeId: 'search-production', pagerDutyServiceId: 'PSEARCH' }],
        },
      ],
    },
  ],
}
