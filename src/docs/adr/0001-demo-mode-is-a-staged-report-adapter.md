# ADR-0001: Demo mode is a staged report adapter

- Status: Accepted
- Date: 2026-07-12

## Context

Production investigations collect live connector results, run recurrence and synthesis, and project those inputs into a report. Demo mode instead advances through deterministic responder-facing snapshots so the live-update experience can be exercised without PostgreSQL or external systems.

An architecture review identified overlap between `DemoIncidentStore` and production report projection. Routing demo mode through production connector inputs would remove some construction overlap, but it would require a broad synthetic-input interface covering connector health, timings, staged AI output, recurrence history, links, and deliberately ordered evidence. That interface would be nearly as complex as the demo implementation and would couple the fixture to production orchestration details.

## Decision

Demo mode remains a staged adapter at the investigation-report reader seam.

It must reuse stable shared policy and contracts where they express real product invariants, including:

- the authoritative investigation report wire contract;
- incident progression names;
- causal-event projection helpers;
- responder-facing domain terminology.

It does not need to route curated snapshots through production evidence connectors, recurrence persistence, durable queues, or synthesis orchestration.

Shared projection logic should be extracted only when production and demo implement the same stable rule behind a smaller interface than the duplicated implementation. Similar-looking fixture construction alone is not sufficient evidence for a new seam.

## Consequences

- Demo sequencing remains deterministic, fast, and independent of external infrastructure.
- The demo may construct report snapshots directly.
- Contract generation and end-to-end tests protect production/demo compatibility.
- Some fixture-specific report construction remains duplicated by design.
- Future reviews should not propose a generalized synthetic connector pipeline without evidence that its interface would be smaller and more stable than the current adapter.
