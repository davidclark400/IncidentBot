# IncidentBot Domain Context

This file defines the domain language and invariants used across IncidentBot. It is a navigation aid, not an architecture decision record. Proposed refactors and implementation details belong elsewhere.

## Product purpose

IncidentBot accepts PagerDuty incident events, collects evidence from configured operational sources, builds a live investigation report, and publishes updates to the web client and Slack.

The investigation must remain useful when a source, recurrence matching, or the required AI synthesis service is unavailable.

## Core terms

### Incident

An operational event received from PagerDuty. An incident has a stable internal ID, a PagerDuty incident ID, service and profile identity, responder-facing state, investigation status, timestamps, labels, and report version.

PagerDuty state and investigation status are different concepts:

- **Incident state** describes the responder lifecycle: triggered, acknowledged, escalated, reassigned, resolved, or unknown.
- **Investigation status** describes IncidentBot's processing progress, such as queued, collecting, ready, or resolved.

An incident may become **frozen** when resolved. Frozen incidents preserve their final responder-facing report while the durable workflow finishes any required finalization.

### Investigation

The durable workflow that turns an incident into an investigation report. It resolves an investigation profile, evaluates recurrence, collects evidence, performs AI synthesis, composes a report, persists it, and publishes versioned updates.

Repeated work for the same incident must be safe. Concurrent report updates use version checks and are retried through the durable work queue.

### Investigation profile

Configuration selected from the incident's service identity and labels. A profile determines which evidence sources are enabled, their allowed scope and transport, and the Slack destination.

Profile revision is included in evidence scope and reports so responders can identify the configuration used for an investigation.

### Evidence source

An operational system from which incident evidence may be collected. Current sources are PagerDuty, Nomad, GitLab, Grafana, and VictoriaLogs.

Each source returns a connector result containing:

- source health;
- evidence findings;
- timeline candidates;
- responder links;
- collection duration;
- a bounded diagnostic when collection is incomplete.

Source health is complete, partial, unavailable, excluded, or pending. Failure of one source must not prevent the other sources or the investigation report from completing.

### Connector

An adapter that collects evidence from one evidence source. A connector may use a native HTTP transport or an MCP transport, subject to the same configured scope, item limits, cumulative source byte limit, timeout policy, and source identity. Exhausting a reserved byte or item budget produces partial source health rather than unbounded collection.

GitLab pipeline collection treats a pipeline as a parent evidence group. Current failed jobs are queried before canceled fanout, hard failed non-allowed job families outrank allowed failures and aggregated downstream cancellations, retries are collapsed without reviving recovered jobs, and trace budgets are shared fairly across selected job families. The earliest hard failure carries a structured ordinal so an upstream failure remains ahead of a closer cascading sibling.

MCP output must prove membership in the profile's allowed resource scope using source-specific structured provenance (for example GitLab project and pipeline, or Nomad namespace and job). Same-host but out-of-scope findings are rejected. Responder URLs are deny-by-default without a trusted root and are retained only when they also match an allowed resource.

Connector responses and external text are untrusted data. Credentials are read from configured environment variables and must not appear in evidence, provenance, diagnostics, logs, fingerprints, or reports.

### Evidence finding

A bounded, source-attributed observation collected during an investigation. A finding includes a deterministic ID, occurrence time, category, severity, summary, confidence, provenance, and optional excerpt, source URL, actor, object identity, and code references.

Evidence IDs and code-reference IDs are stable citation targets for synthesis and report presentation.

Evidence severity, confidence, relevance, and presentation priority are distinct concepts. High-volume groups must not crowd independent sources out of the report, Slack, or synthesis input.

### Timeline candidate

A source-attributed event that may appear in the incident timeline. Timeline order indicates chronology, not causation.

### Causal event

A report projection of evidence that may help explain the incident's candidate causal sequence. Causal language must remain evidence-grounded; chronology alone is not proof of causation.

### Investigation report

The versioned responder-facing projection of an investigation. It contains deterministic status and summary information, evidence, timeline, source health, links, AI synthesis status and output, causal events, and recurrence context.

The report is the primary wire contract shared by the API, web client, Slack publishing, persistence, and demo mode. Contract changes must remain backwards-compatible with older persisted reports unless an explicit migration is provided.

### AI synthesis

A required, evidence-grounded interpretation attempted from a bounded digest of collected evidence. LiteLLM failure is represented explicitly and must not suppress the deterministic report. It may summarize evidence, rank supported diagnoses, record unknowns, and recommend verification checks.

The digest uses exact-ID deduplication and ranked source diversity by default. Narrow template compression is applied only when the exact digest would exceed its configured input limit, and only to repetitive VictoriaLogs samples and repeated Nomad allocation failures. Failed pipeline steps, first-error anchors, metrics, change evidence, and code-bearing findings remain independently citable.

AI synthesis must not determine incident identity, fingerprint equality, problem-group association, or lifecycle state. Only evidence IDs, code-reference IDs, and summary references actually serialized into the bounded digest may be returned; unknown or budget-omitted identifiers are discarded rather than trusted. Model response envelopes are streamed under a strict byte limit.

### Fingerprint

A deterministic, versioned representation of stable incident features. Dynamic IDs, counts, timestamps, secrets, and unbounded raw evidence must not influence identity.

- A **provisional fingerprint** uses the PagerDuty incident and stable labels before evidence collection completes. It may suggest historical matches.
- A **final fingerprint** incorporates the complete accumulated evidence set and is authoritative when evidence collection is enabled.
- A **family fingerprint** groups incidents with the same stable failure family.
- An **exact fingerprint** represents the strongest deterministic equality signal within an algorithm version.

Fingerprint algorithm versions are isolated. Different versions must not silently compare as exact matches.

### Recurrence

The process of comparing an incident fingerprint with historical candidates and associating the incident with a persistent problem group. Matching must be deterministic and explainable through a match type, score, and matched features.

Recurrence failure must not prevent the main investigation report from being saved. The report records recurrence as unavailable with a bounded diagnostic.

### Problem group

A persistent grouping of recurring incidents that share a deterministic failure identity. A problem group has a human-readable problem key, algorithm version, representative fingerprint, occurrence history, and lifecycle state.

Problem lifecycle is new, ongoing, resolved, regressed, or escalating. Lifecycle derives from persisted occurrences and incident states, not from AI synthesis.

### Durable work item

Persisted work that runs or repeats an investigation. Work items are leased, retried after failure, and completed idempotently where possible.

### Outbox item

Persisted publication work, currently used for Slack report updates. Report persistence and outbox creation occur together so publication can be retried without losing the committed report.

### Slack report publication

A durable intent to refresh an incident's single Slack message from the latest committed investigation report. Delivery is at-least-once: a returned Slack timestamp confirms creation and identifies repeatable updates, while an initial post whose response is lost may be retried. A delayed stuck check applies only while its originating report version remains current and collecting.

### Demo mode

A self-contained adapter used to demonstrate the live report experience without PostgreSQL, external evidence systems, or recurrence persistence. Demo reports should remain representative of production contracts and responder concepts rather than define separate product behavior.

## System invariants

1. An accepted PagerDuty event is processed idempotently.
2. Evidence collection is bounded by configured time, item, and byte limits.
3. External content is untrusted and cannot supply instructions to AI synthesis.
4. A connector, recurrence, or synthesis failure does not suppress the deterministic report.
5. Report versions increase monotonically; stale updates must not replace newer reports.
6. Fingerprinting and problem-group association are deterministic and independent of LLM output.
7. Secrets and authentication material never enter retained evidence or fingerprint features.
8. Slack publication is retriable through the outbox.
9. Demo mode preserves the production report contract.
10. Retention may delete full incident evidence before compact fingerprint and problem history.

## Current architecture map

- `IncidentBot.Api/Incidents` — incident intake endpoints, investigation orchestration, progression policy, report projection, live updates, Slack publication, and durable workers.
- `IncidentBot.Api/Connectors` — the evidence-source registry plus native and MCP evidence adapters.
- `IncidentBot.Api/Fingerprinting` — the recurrence interface, deterministic feature extraction, normalization, generation, matching, graceful degradation, and problem persistence.
- `IncidentBot.Api/Infrastructure` — PostgreSQL schema initialization, incident/report persistence, durable queues, and deployment readiness.
- `IncidentBot.Api/Profiles` — investigation profile models, loading, selection, and validation.
- `IncidentBot.Api/Domain` — shared domain records used by investigation, persistence, and wire output.
- `IncidentBot.Api/Demo` — self-contained demo adapters and staged report data.
- `IncidentBot.Client` — live investigation report client and SignalR session handling.

## Established architecture seams

- `IncidentProgression` owns investigation status names and responder-visible progression decisions while persisted reports retain backwards-compatible string values.
- `EvidenceSourceRegistry` owns the five-source roster, profile-to-transport lookup, connector registration, and enabled-source selection. Source-specific collection and validation remain with their adapters and profile models.
- `EvidenceRankingPolicy` owns responder-facing evidence relevance, grouping, source diversity, and deterministic ordering. Report, AI, Slack, and connector truncation must use this shared policy rather than treating severity as presentation priority.
- `IRecurrenceCoordinator` is the investigation workflow's recurrence interface. It owns provisional/final orchestration and unavailable-state mapping; the runner does not coordinate fingerprint construction and matching directly.
- `RecurrencePolicy` owns candidate ranking, association thresholds, problem keys, time cutoffs, and lifecycle classification. PostgreSQL persistence owns locking and storage.
- `IIncidentStore` is the domain-facing incident persistence interface. Intake, investigation, Slack, retention, and report reading do not depend on the PostgreSQL implementation.
- `IncidentBot.Contracts` is the authoritative report wire contract. OpenAPI generation and generated TypeScript types keep the client representation aligned.
- Client modules are organized by feature. `App.tsx` only selects the landing or incident feature; the incident page owns live-session orchestration, while report modules own responder-concept derivation and rendering for recurrence, evidence review, and analysis.

## Remaining architectural friction

These are observations, not accepted decisions:

- Demo mode intentionally constructs staged report snapshots rather than simulating production connector inputs; see `docs/adr/0001-demo-mode-is-a-staged-report-adapter.md`.

Use an ADR when resolving one of these points if the chosen direction is non-obvious, constrains future work, or rejects a plausible alternative.
