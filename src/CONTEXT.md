# Panko Domain Context

Panko helps responders turn operational signals into a durable, source-grounded understanding of an active issue. Its language follows the product metaphor: individual observations are crumbs, their ordered story is a trail, and the complete responder view is a case file.

## Language

### Core casework

**Case**:
The primary Panko aggregate for one operational issue. A case may originate from a PagerDuty incident, an agent, or a responder, and owns its inputs, processing state, Case File, and team identity.

**Case origin**:
The trusted channel that opened a Case and, when present, its external identity. PagerDuty, agent, and manual are the supported origins.

**Case state**:
The responder lifecycle of a Case, distinct from Panko's processing progress. For a PagerDuty-originated Case it reflects the PagerDuty incident lifecycle.

**Case admission**:
The idempotent acceptance of an origin event or snapshot into a Case. Admission selects a Recipe, retains only approved labels, snapshots team ownership, and schedules durable work.

**Case progress**:
The live, lightweight view of work currently being performed for a Case. It describes collection, synthesis, and publication without duplicating the Case File's Crumbs.

**Case File**:
The versioned responder-facing projection of a Case. It contains deterministic status and summary information, Crumbs, a Trail, source health, links, AI synthesis, causal context, and Pattern context.

**Case File transition**:
A committed move from one Case File version to the next. Versions increase monotonically, and a stale transition cannot replace a newer Case File.

### Collected knowledge

**Crumb**:
A bounded, source-attributed atomic observation collected or submitted for a Case. A Crumb has a stable identity, occurrence time, category, severity, summary, confidence, provenance, and optional supporting detail.

**Crumb source**:
An operational system from which Panko may collect Crumbs. Current sources are PagerDuty, Nomad, Consul, GitLab, Grafana, Kafka, and VictoriaLogs.

**Crumb source health**:
The collection outcome for one Crumb source: pending, complete, partial, unavailable, or excluded. One unhealthy source cannot prevent the Case File from completing.

**Adaptive crumb window**:
A bounded collection period that expands into older, non-overlapping rings while accumulated Crumbs remain deterministically inconclusive. Stable snapshots are not repeatedly queried, and all retained results remain within item and byte limits.

**Trail**:
The ordered Case File projection of source-attributed events that help responders understand what changed. Trail order establishes chronology, not causation.

**Causal marker**:
A Case File projection of a Crumb that may help explain a candidate causal sequence. A causal marker must remain source-grounded and cannot turn chronology alone into a causal claim.

### Configuration and identity

**Recipe**:
Configuration selected from a Case's observed service identity and approved labels. A Recipe defines enabled Crumb sources, allowed resource scope, team ownership, and publication destination; endpoints and transports remain deployment configuration.

**Observed service**:
The responder-owned workload represented by one Recipe in one deployment environment. It may have many distributed instances and is distinct from a PagerDuty service or Consul service.

**Service collection**:
A team-owned grouping of observed services that responders browse and operate together. Its identity is scoped to its team, and each observed service belongs to exactly one collection.

**Team ownership**:
The canonical authorization owner of a service collection, Recipe, Case, and Pattern. A Case snapshots its Recipe's team at admission, so later Recipe changes cannot transfer its history.

**Signature**:
A deterministic, versioned representation of stable Case features used for equality and Pattern matching. Dynamic identifiers, timestamps, secrets, and unbounded source content cannot influence a Signature.

**Provisional signature**:
A Signature built before Crumb collection completes. It may suggest historical matches but is not authoritative when collection is enabled.

**Final signature**:
The authoritative Signature built from the complete retained Crumb set for a Case.

**Family signature**:
A Signature value that identifies a stable failure family across Cases.

**Exact signature**:
The strongest deterministic equality value within one Signature algorithm version. Values from different algorithm versions cannot silently compare as exact.

**Pattern**:
A persistent, team-owned grouping of recurring Cases that share a deterministic failure identity. A Pattern records its key, representative Signature, occurrence history, and lifecycle.

**Pattern match**:
An explainable comparison between a Case Signature and historical Pattern candidates. It records the match type, score, and matched features independently of AI synthesis.

**Pattern lifecycle**:
The deterministic state of a Pattern: new, ongoing, resolved, regressed, or escalating. It derives from persisted Case occurrences and states.

### Supporting concepts

**AI synthesis**:
A required, source-grounded interpretation attempted from a bounded digest of Crumbs. Failure is explicit and cannot suppress the deterministic Case File or determine Case identity, Signature equality, Pattern association, or lifecycle.

**Durable work item**:
Persisted work that runs or repeats Case processing. Work items are leased, retried after failure, and completed idempotently where possible.

**Outbox item**:
Persisted publication work created with a Case File transition so delivery can be retried without losing the committed Case File.

**Slack Case File publication**:
A durable intent to refresh a Case's single Slack message from its latest committed Case File. Delivery is at-least-once and repeat updates target the confirmed Slack message.

**Security audit event**:
An immutable record of a security-sensitive access decision or request. It identifies the action, outcome, actor, target team, and resource without retaining Case File content, credentials, or prompt text.

**Demo mode**:
A self-contained adapter that demonstrates the live Case File experience without external operational systems. Demo behavior preserves production responder concepts and contracts.

**Demo replay**:
One generation of staged Case File transitions in Demo mode. A reset starts a newer generation, and transitions from an older generation cannot overwrite it.
