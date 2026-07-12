# Incident Fingerprinting and Recurrence Detection

## Goal-mode objective

Implement deterministic, explainable incident fingerprinting and recurrence detection in IncidentBot. Similar PagerDuty incidents must be associated with a persistent problem group, while materially different incidents must remain separate. Surface recurrence history and lifecycle state in the API, web UI, and Slack report without using LLM output to determine incident identity.

The implementation must preserve all existing incident collection, reporting, demo-mode, and retention behaviour unless this specification explicitly changes it.

## Product outcome

When IncidentBot investigates an incident, responders should immediately be able to tell:

- Whether this is a new problem or a recurrence.
- Whether a previously resolved problem has regressed.
- How many times the problem has occurred.
- When it was first and most recently seen.
- Which stable features caused IncidentBot to associate the incidents.
- How confident IncidentBot is in the association.

Example presentation:

> Regressed problem · 90% match · 4 occurrences in 30 days  
> Matched on provider timeout, checkout component, and `ProviderClient.SendAsync`.

## Existing architecture

IncidentBot is an ASP.NET Core application with a React client and PostgreSQL persistence.

Relevant implementation points:

- `IncidentBot.Api/Domain/Models.cs` contains incident, evidence, and report contracts.
- `IncidentBot.Api/Infrastructure/DatabaseInitializer.cs` creates the PostgreSQL schema.
- `IncidentBot.Api/Infrastructure/IncidentRepository.cs` persists incidents and versioned reports.
- `IncidentBot.Api/Incidents/InvestigationRunner.cs` coordinates evidence collection and report generation.
- `IncidentBot.Api/Incidents/ReportComposer.cs` combines findings into the report.
- `IncidentBot.Api/Incidents/LiteLlmSynthesizer.cs` produces evidence-grounded diagnoses.
- `IncidentBot.Api/Incidents/SlackPublisher.cs` publishes reports to Slack.
- `IncidentBot.Client/src/incidentReport.ts` defines the client report contract.
- `IncidentBot.Client/src/App.tsx` renders the investigation report.

Evidence currently comes from PagerDuty, Nomad, GitLab, Grafana, and VictoriaLogs. Evidence findings include source, category, severity, summary, excerpt, object identity, actor, URL, confidence, and optional code references.

## Terminology

### Incident

A single PagerDuty incident represented by the existing `incidents` table.

### Problem group

A persistent identity that connects separate incidents believed to represent the same underlying operational problem.

### Provisional fingerprint

An early fingerprint derived from the PagerDuty incident and stable labels before evidence collection completes. It may suggest historical matches but must not be the final authority when collected evidence is available.

### Final fingerprint

The authoritative fingerprint derived from the incident plus accumulated evidence after collection.

### Family fingerprint

A broad symptom identity, such as `payments-api + production + checkout + HTTP 5xx`.

### Exact fingerprint

A narrower identity including stable error templates, dependencies, or code locations, such as `provider timeout in ProviderClient.SendAsync`.

## Functional requirements

### 1. Deterministic feature extraction

Create a dedicated fingerprinting subsystem under `IncidentBot.Api/Fingerprinting`.

It must extract a structured set of stable features from an incident and its evidence:

- Service ID and profile ID.
- Environment, region, namespace, or equivalent stable scope labels when available.
- Normalized incident title.
- Symptom categories.
- Normalized error templates.
- Affected components, workloads, or dependencies.
- Stable code locations, preferring project/path/member identity over mutable line numbers.

The extractor must not use AI or LLM output.

Do not include the following in the primary identity:

- Incident ID or PagerDuty incident ID.
- Timestamps or durations.
- Event counts or metric values.
- Request, trace, allocation, or object instance IDs.
- Commit SHA, merge-request number, deployment ID, or suspected change.
- Actor identity.
- URLs containing resource-specific identifiers.
- Evidence confidence values.

Suspected changes remain evidence about cause, not part of the recurring symptom identity.

### 2. Normalization

Implement bounded, deterministic text normalization that at minimum recognizes and replaces:

- UUIDs.
- Long hexadecimal identifiers and commit hashes.
- IPv4 and IPv6 addresses.
- Ports where they are paired with hosts.
- ISO and common log timestamps.
- Durations and timestamps expressed numerically.
- Request, trace, span, allocation, job-instance, order, and resource IDs.
- Numeric counts.
- HTTP status codes with a stable status family where appropriate.
- Dynamic path and query-string segments.

Examples:

```text
allocation 75ca81 failed after 31.4 seconds
=> allocation <id> failed after <duration>

POST /payments/ord_78431 returned 502
=> post /payments/<id> returned <5xx>

timeout connecting to 10.23.4.18:5432
=> timeout connecting to <ip>:<port>
```

Normalization must:

- Use invariant casing and whitespace rules.
- Place strict length and item-count bounds on stored features.
- Apply existing configured log redaction before persistence where applicable.
- Never persist credentials or secrets discovered in evidence.
- Produce the same output regardless of input collection order.

### 3. Fingerprint generation

Define domain contracts equivalent to:

- `FingerprintFeatures`
- `IncidentFingerprint`
- `ProblemGroupSummary`
- `ProblemMatch`
- `ProblemOccurrenceSummary`

The precise names may change to match repository conventions.

Each fingerprint must contain:

- An explicit algorithm version, initially `v1`.
- Stage: provisional or final.
- Canonical family hash.
- Canonical exact hash.
- Bounded structured features used to calculate the hashes.
- Extraction confidence or completeness indicator.

Build canonical strings from ordinal-sorted, normalized feature sets and hash them with SHA-256. Hash equality must never depend on dictionary, connector, evidence, or database ordering.

Algorithm versions must be isolated. A `v2` fingerprint must not silently compare as an exact match with `v1`.

### 4. Persistent problem groups

Add idempotent PostgreSQL schema for three concepts:

#### `problem_groups`

Store:

- Stable group ID.
- Service and profile scope.
- Current family and representative exact hashes.
- Lifecycle state.
- First-seen, last-seen, and resolved timestamps.
- Occurrence count.
- Created and updated timestamps.

#### `incident_fingerprints`

Store:

- Incident ID.
- Algorithm version.
- Fingerprint stage.
- Family and exact hashes.
- Structured normalized features as JSONB.
- Completeness/confidence.
- Creation timestamp.

There must be at most one fingerprint per incident, algorithm version, and stage.

#### `problem_occurrences`

Store:

- Problem group ID.
- Incident ID.
- Match type and similarity score.
- Explainable matched-feature summary as JSONB.
- Active/resolved state or sufficient information to derive it.
- Creation and updated timestamps.

An incident may belong to at most one automatic problem group for a given algorithm version.

Add indexes supporting exact hash, family hash, scoped recent-candidate lookup, and group history retrieval.

All grouping writes must be safe when multiple matching incidents are processed concurrently. Prevent duplicate problem groups for the same scoped exact identity through database constraints or transactional locking.

### 5. Matching rules

Match in this order:

1. Restrict candidates by algorithm version, service, and profile.
2. Apply stable environment or equivalent scope as a hard boundary when both sides provide it.
3. Prefer an exact-hash match.
4. Then consider a family-hash match.
5. Then calculate weighted similarity across structured features.
6. Create a new problem group when no candidate reaches the automatic threshold.

Initial similarity weights:

| Feature | Weight |
| --- | ---: |
| Error templates or exception identity | 35 |
| Stable code locations | 25 |
| Components or dependencies | 15 |
| Symptom categories | 15 |
| Normalized title tokens | 10 |

Initial decision thresholds:

- `80-100`: automatically associate with the best problem group.
- `60-79`: expose as a possible historical match, but do not automatically associate.
- Below `60`: create a new problem group.

Exact-hash matches have a score of 100. Family-hash equality alone must not bypass a conflicting exact symptom when adequate evidence exists.

Resolve equal-score ambiguity deterministically using, in order:

1. Exact-hash match.
2. Highest similarity.
3. Most recent occurrence.
4. Stable problem-group ID ordering.

Make weights, thresholds, candidate lookback, and maximum candidate count configurable under the existing `IncidentBot` configuration section with validated safe defaults.

### 6. Investigation workflow

Integrate fingerprinting into `InvestigationRunner`.

Required sequence:

1. Load the incident and resolved investigation profile.
2. Build and persist a provisional fingerprint.
3. Retrieve possible provisional historical matches for early display, without making a final cross-incident identity decision when collection remains enabled.
4. Collect evidence through the existing connectors.
5. Combine newly collected evidence with evidence already present in the previous report.
6. Build and persist the final fingerprint from the complete accumulated evidence set.
7. Match or create the authoritative problem group transactionally.
8. Add problem and recurrence context to report composition.
9. Save and publish the report through existing SignalR and Slack flows.

If evidence collection is disabled, use the provisional fingerprint as the authoritative fingerprint and mark its lower completeness in the report.

Fingerprinting failure must not prevent the main investigation report from being saved. Report fingerprinting as unavailable with a bounded diagnostic, log the exception, and preserve the existing durable retry semantics where safe.

Repeated investigation runs must be idempotent: they must update the same occurrence rather than incrementing the occurrence count repeatedly.

### 7. Problem lifecycle

Support these states:

- `new`: first recorded occurrence.
- `ongoing`: recurrence while the group is unresolved or after the first occurrence has been reviewed.
- `resolved`: no grouped incident remains active.
- `regressed`: a new occurrence matches a previously resolved group.
- `escalating`: occurrence rate crosses a configured deterministic threshold.

For the initial implementation, escalation may use a simple rule such as at least three distinct incidents within seven days. Do not implement statistical forecasting in this goal.

PagerDuty resolution handling must update the associated occurrence. A problem group becomes resolved only when no occurrence in that group remains active. A later matching incident transitions it to regressed.

Lifecycle updates must be idempotent for duplicate PagerDuty webhook events.

### 8. Report and API contract

Extend `InvestigationReport` with optional, backwards-compatible fingerprinting data containing:

- Human-readable problem key.
- Algorithm version and fingerprint stage.
- Problem group ID.
- Lifecycle state.
- Match type and score.
- Matched-feature explanation.
- Occurrence count.
- First-seen and last-seen timestamps.
- A bounded list of recent occurrences.
- Possible historical matches that did not reach the automatic threshold.
- Availability/completeness status.

Do not expose the raw normalized evidence corpus. Expose only bounded, responder-readable match explanations.

Existing stored reports without the new fields must continue to deserialize and render.

### 9. Human-readable problem key

Generate a stable display key derived from safe normalized attributes and a short hash suffix, for example:

```text
PAYMENTS-CHECKOUT-4F19
```

The display key is not the database identity and must remain unique enough for responder communication. Raw SHA-256 hashes should not be the primary UI identity.

### 10. Web UI

Update the TypeScript report contract and React UI to show:

- New, recurring, regressed, escalating, or resolved badge.
- Human-readable problem key.
- Automatic match percentage and match type.
- A short explanation of the matched stable features.
- Occurrence count plus first and last seen.
- Recent occurrence history, including state and link to retained reports.
- Possible related incidents separately from authoritative group history.
- A clear unavailable or provisional state while fingerprinting is incomplete.

The UI must remain usable when all new fields are absent.

Use existing visual language and responsive layout. Do not introduce a new component library.

### 11. Slack output

Update the Slack report when fingerprint data is available. Keep it concise:

```text
Regressed problem PAYMENTS-CHECKOUT-4F19
90% match · 4 occurrences · last seen 14 June
Matched on provider timeout, checkout component, ProviderClient.SendAsync
```

Do not create additional Slack messages solely because a provisional fingerprint was replaced by a final fingerprint; update through the existing outbox/message flow.

### 12. Retention

Full report/evidence retention currently defaults to 30 days. Recurrence detection needs a longer compact history.

Add a validated `FingerprintRetentionDays` option with a default of 365 days. Retain only the bounded normalized fingerprint, group metadata, and minimal occurrence summary after full incident evidence expires.

Do not retain raw excerpts or complete reports beyond their existing retention period merely to support fingerprinting.

Update retention logic so:

- Full incident/report/evidence data follows the existing retention setting.
- Compact fingerprint/problem history follows fingerprint retention.
- Expired groups without retained occurrences are safely removed.

### 13. Observability

Add structured logs for:

- Fingerprint stage and algorithm version.
- Exact, family, similarity, or new-group decision.
- Selected problem group and similarity score.
- Candidate count.
- Lifecycle transitions.
- Normalization or persistence failures without logging sensitive normalized content.

Do not log full evidence, excerpts, secrets, or raw normalized templates.

## Non-functional requirements

- All matching is deterministic and explainable.
- No LLM output affects hashes, matching, grouping, or lifecycle state.
- Candidate lookup and comparison are bounded.
- Fingerprinting adds no unbounded database scan.
- Processing is safe under concurrent worker execution.
- Existing webhook idempotency and report optimistic concurrency remain intact.
- Existing demo mode continues to run without requiring PostgreSQL fingerprint persistence; provide representative demo fingerprint data or a safe no-op implementation.
- New JSON fields are optional for backwards compatibility.
- All timestamps use UTC.
- All string comparisons used for identity are ordinal after normalization.

## Security and privacy requirements

- Reuse configured redaction rules where possible.
- Never persist authentication headers, credentials, tokens, passwords, or connection-string secrets in fingerprint features.
- Treat all connector evidence as untrusted input.
- Bound every stored string and collection.
- Do not render normalized content as HTML.
- Do not expose internal database IDs where a safe public problem key is sufficient.

## Out of scope

- LLM-driven incident identity or clustering.
- Vector embeddings or external vector databases.
- Statistical anomaly forecasting.
- Automatic merging across different services.
- Automated code fixes or merge requests.
- A complete operator UI for manual merge and split.
- Backfilling every historical report in the first release.
- Changing the existing evidence connector protocols.

Design the persistence and service boundaries so manual merge/split and controlled backfill can be added later.

## Implementation plan

### Step 1: Establish tests and contracts

- Add or extend an API test project using the repository's target framework.
- Add domain contracts and validated fingerprinting options.
- Write normalization and deterministic hashing tests before workflow integration.

### Step 2: Implement normalization and feature extraction

- Implement bounded normalization.
- Extract stable features from incident fields and evidence findings.
- Ensure order-independent canonicalization.
- Add security and redaction tests.

### Step 3: Implement hashing and similarity matching

- Generate versioned family and exact hashes.
- Implement weighted, explainable similarity.
- Test exact, family, ambiguous, conflicting, and below-threshold cases.

### Step 4: Add database schema and repository

- Add idempotent tables, constraints, and indexes.
- Implement transactional candidate lookup, group creation, occurrence association, and lifecycle updates.
- Add PostgreSQL integration tests, including concurrent group creation.

### Step 5: Integrate investigation flow

- Register fingerprinting services in `Program.cs`.
- Add provisional and final fingerprint stages to `InvestigationRunner`.
- Make repeated runs idempotent.
- Degrade safely when fingerprinting is unavailable.

### Step 6: Integrate PagerDuty resolution and lifecycle

- Update associated occurrences when incidents resolve or reopen.
- Implement new, ongoing, resolved, regressed, and simple escalating transitions.
- Verify duplicate webhook handling.

### Step 7: Extend report, SignalR, and Slack contracts

- Add optional report fields.
- Add problem context to report composition.
- Publish fingerprint/history section changes.
- Add concise Slack recurrence context.

### Step 8: Implement the client UI

- Extend TypeScript types.
- Add recurrence badge, match explanation, and occurrence history.
- Handle absent, provisional, final, and unavailable states.
- Verify responsive and dark-mode layouts.

### Step 9: Implement independent retention

- Add fingerprint retention configuration.
- Preserve compact history when full evidence expires.
- Delete expired orphaned data safely.

### Step 10: Verify and document

- Run API and client tests.
- Build both projects.
- Exercise demo mode.
- Document configuration and lifecycle behaviour.
- Summarize schema changes, matching rules, and future extension points.

## Required test scenarios

At minimum, automate these scenarios:

1. Titles differing only in counts produce the same normalized form.
2. UUIDs, allocation IDs, request IDs, IPs, ports, timestamps, and durations normalize consistently.
3. Input ordering does not change either hash.
4. The same symptom in different services does not group automatically.
5. Conflicting environments do not group automatically when both are known.
6. Matching error templates and code locations produce a high score.
7. A family match with conflicting exact symptoms does not receive an unjustified automatic match.
8. Changing the suspected commit does not change the fingerprint.
9. Repeated investigation runs do not create additional occurrences.
10. Concurrent matching incidents create or select one problem group.
11. A resolved group becomes regressed when a new matching incident arrives.
12. A group remains active while any occurrence is active.
13. Three distinct occurrences within the configured window become escalating.
14. Old reports without fingerprint fields still deserialize and render.
15. Missing or failed fingerprinting does not prevent a report from being produced.
16. Sensitive values do not appear in stored features, logs, API responses, or Slack output.
17. Full evidence can expire while compact recurrence history remains available.
18. Demo mode remains functional.

## Acceptance criteria

The goal is complete only when all of the following are true:

- Similar incidents with dynamic IDs, counts, and timestamps receive the same deterministic fingerprint.
- Materially different incidents in the same service remain separate.
- Different services never group automatically.
- Every automatic association includes an understandable matched-feature explanation.
- Exact and similarity decisions are stable across repeated runs and collection ordering.
- A resolved group is labelled regressed when a matching incident returns.
- Problem occurrence counts are idempotent and concurrency-safe.
- The API, web UI, and Slack report expose useful recurrence context.
- Existing reports and demo mode remain compatible.
- Fingerprinting failures degrade gracefully without losing the main investigation report.
- Compact recurrence history outlives raw evidence according to separate retention settings.
- No AI output participates in identity decisions.
- Automated tests cover the required scenarios and pass.
- API and client production builds pass.

## Verification

Discover and use the repository's existing build and test entry points. At minimum, verify the equivalent of:

```bash
dotnet test
dotnet build IncidentBot.Api/IncidentBot.Api.csproj
npm test --prefix IncidentBot.Client
npm run build --prefix IncidentBot.Client
```

If the client has no test script, add focused tests using the existing client toolchain or document and perform an appropriate alternative. Do not declare completion based only on compilation.

Manually or through an integration test demonstrate:

1. Two incidents whose titles and logs differ only by dynamic values grouping together.
2. A different failure in the same service remaining separate.
3. Resolution followed by recurrence producing the regressed state.
4. The match explanation appearing in the report UI and Slack rendering.

## Goal-mode working instructions

- Inspect all applicable `AGENTS.md` files before editing files in their scope.
- Preserve unrelated user changes in the working tree.
- Use small, reviewable changes and verify after each major phase.
- Prefer deterministic pure functions for normalization, canonicalization, and scoring.
- Keep persistence and matching behind interfaces so demo mode and future manual controls remain straightforward.
- Do not stop after scaffolding or partial implementation. Continue until the acceptance criteria and verification requirements are satisfied, or report a concrete external blocker with evidence.
- At completion, provide a concise summary of behaviour, files changed, schema/config additions, tests run, and any deliberately deferred out-of-scope work.
