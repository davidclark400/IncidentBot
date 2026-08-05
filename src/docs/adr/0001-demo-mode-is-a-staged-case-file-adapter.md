# ADR-0001: Demo mode is a staged Case File adapter

- Status: Accepted
- Date: 2026-07-12

## Context

Production Cases collect live Crumb-source results, match Patterns, run synthesis, and project those inputs into a Case File. Demo mode instead advances through deterministic responder-facing snapshots so the live-update experience can be exercised without PostgreSQL or external systems.

An architecture review identified overlap between the demo store and production Case File projection. Routing demo mode through production Crumb-source inputs would remove some construction overlap, but it would require a broad synthetic-input interface covering Crumb-source health, timings, staged AI output, Pattern history, links, and deliberately ordered Crumbs. That interface would be nearly as complex as the demo implementation and would couple the fixture to production orchestration details.

## Decision

Demo mode remains a staged adapter at the Case File reader seam.

It must reuse stable shared policy and contracts where they express real product invariants, including:

- the authoritative Case File wire contract;
- Case progression names;
- causal-marker projection helpers;
- responder-facing domain terminology.

It does not need to route curated snapshots through production Crumb sources, Pattern persistence, durable queues, or synthesis orchestration.

Shared projection logic should be extracted only when production and demo implement the same stable rule behind a smaller interface than the duplicated implementation. Similar-looking fixture construction alone is not sufficient evidence for a new seam.

## Consequences

- Demo sequencing remains deterministic, fast, and independent of external infrastructure.
- The demo may construct Case File snapshots directly.
- Contract generation and end-to-end tests keep production and demo on the same Case File contract.
- Some fixture-specific Case File construction remains duplicated by design.
- Future reviews should not propose a generalized synthetic Crumb-source pipeline without proof that its interface would be smaller and more stable than the current adapter.
