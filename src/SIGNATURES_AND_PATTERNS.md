# Case Signatures and Pattern Detection

## Goal-mode objective

Implement deterministic, explainable Signature generation and Pattern detection in Panko. Cases opened from similar PagerDuty incidents must be associated with a persistent Pattern, while materially different Cases must remain separate. Surface Pattern history and lifecycle state in the API, web UI, and Slack Case File without using LLM output to determine Case identity.

The implementation must preserve existing Crumb collection, Case File generation, demo-mode, and retention behaviour unless this specification explicitly changes it.

## Product outcome

When Panko processes a Case, responders should immediately be able to tell:

- Whether this is a new Pattern or a recurrence.
- Whether a previously resolved Pattern has regressed.
- How many times the Pattern has occurred.
- When it was first and most recently seen.
- Which stable features caused Panko to associate the Cases.
- How confident Panko is in the association.

Example presentation:

> Regressed Pattern · 90% match · 4 occurrences in 30 days
> Matched on provider timeout, checkout component, and `ProviderClient.SendAsync`.

## Existing architecture

Panko is an ASP.NET Core application with a React client and PostgreSQL persistence.

Relevant implementation points:

- `Panko.Api/Domain/Models.cs` contains Case, Crumb, Signature, Pattern, and Case File contracts.
- `Panko.Api/Infrastructure/DatabaseInitializer.cs` creates the PostgreSQL schema.
- `Panko.Api/Infrastructure/PostgresCaseStore.cs` persists Cases and versioned Case Files.
- `Panko.Api/CaseFiles/CaseFileBuilder.cs` coordinates Crumb collection and Case File generation.
- `Panko.Api/CaseFiles/CaseFileComposer.cs` combines Crumbs into the Case File.
- `Panko.Api/CaseFiles/LiteLlmSynthesizer.cs` produces Crumb-grounded diagnoses.
- `Panko.Api/Cases/SlackPublisher.cs` publishes Case Files to Slack.
- `Panko.Client/src/caseFile.ts` defines the client Case File contract.
- `Panko.Client/src/features/cases/CaseFile.tsx` renders the Case File.

Crumbs currently come from PagerDuty, Nomad, Consul, GitLab, Grafana, Kafka, and VictoriaLogs. Each Crumb can include source, category, severity, summary, excerpt, object identity, actor, URL, confidence, provenance, and optional code references.

## Terminology

### Case

Panko's aggregate for one operational issue. A PagerDuty-originated Case maps to one PagerDuty incident.

### Pattern

A persistent, team-owned grouping of recurring Cases that share a deterministic failure identity.

### Provisional Signature

An early Signature derived from the Case origin and stable labels before Crumb collection completes. For a PagerDuty-originated Case, the origin includes the PagerDuty incident. A provisional Signature may suggest historical matches but is not authoritative when collected Crumbs are available.

### Final Signature

The authoritative Signature derived from the Case plus accumulated Crumbs after collection.

### Family Signature

A broad symptom identity, such as `payments-api + production + checkout + HTTP 5xx`.

### Exact Signature

A narrower identity including stable error templates, dependencies, or code locations, such as `provider timeout in ProviderClient.SendAsync`.

## Functional requirements

### 1. Deterministic feature extraction

Keep deterministic feature extraction and hashing under `Panko.Api/Signatures`, with matching and lifecycle policy under `Panko.Api/Patterns`.

It must extract a structured set of stable features from a Case and its Crumbs:

- Service ID and Recipe ID.
- Environment, region, namespace, or equivalent stable scope labels when available.
- Normalized Case title.
- Symptom categories.
- Normalized error templates.
- Affected components, workloads, or dependencies.
- Stable code locations, preferring project/path/member identity over mutable line numbers.

The extractor must not use AI or LLM output.

Do not include the following in the primary identity:

- Case ID or PagerDuty incident ID.
- Timestamps or durations.
- Event counts or metric values.
- Request, trace, allocation, or object instance IDs.
- Commit SHA, merge-request number, deployment ID, or suspected change.
- Actor identity.
- URLs containing resource-specific identifiers.
- Crumb confidence values.

Suspected changes remain Crumbs about possible cause, not part of the recurring symptom identity.

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
- Never persist credentials or secrets discovered in Crumbs.
- Produce the same output regardless of input collection order.

### 3. Signature generation

Define domain contracts equivalent to:

- `SignatureFeatures`
- `CaseSignature`
- `PatternContext`
- `PatternMatch`
- `PatternOccurrenceSummary`

The precise names may change to match repository conventions.

Each Signature must contain:

- An explicit algorithm version, initially `v1`.
- Stage: provisional or final.
- Canonical family hash.
- Canonical exact hash.
- Bounded structured features used to calculate the hashes.
- Extraction confidence or completeness indicator.

Build canonical strings from ordinal-sorted, normalized feature sets and hash them with SHA-256. Hash equality must never depend on dictionary, Crumb source, Crumb, or database ordering.

Algorithm versions must be isolated. A `v2` Signature must not silently compare as an exact match with `v1`.

### 4. Persistent Patterns

Add idempotent PostgreSQL schema for three concepts:

#### Patterns (`patterns`)

Store:

- Stable Pattern ID.
- Service and Recipe scope.
- Current family and representative exact hashes.
- Lifecycle state.
- First-seen, last-seen, and resolved timestamps.
- Occurrence count.
- Created and updated timestamps.

#### Signatures (`case_signatures`)

Store:

- Case ID.
- Algorithm version.
- Signature stage.
- Family and exact hashes.
- Structured normalized features as JSONB.
- Completeness/confidence.
- Creation timestamp.

There must be at most one Signature per Case, algorithm version, and stage.

#### Pattern occurrences (`pattern_occurrences`)

Store:

- Pattern ID.
- Case ID.
- Match type and similarity score.
- Explainable matched-feature summary as JSONB.
- Active/resolved state or sufficient information to derive it.
- Creation and updated timestamps.

A Case may belong to at most one automatic Pattern for a given algorithm version.

Add indexes supporting exact hash, family hash, scoped recent-candidate lookup, and Pattern history retrieval.

All grouping writes must be safe when multiple matching Cases are processed concurrently. Prevent duplicate Patterns for the same scoped exact identity through database constraints or transactional locking.

### 5. Matching rules

Match in this order:

1. Restrict candidates by algorithm version, service, and Recipe.
2. Apply stable environment or equivalent scope as a hard boundary when both sides provide it.
3. Prefer an exact-hash match.
4. Then consider a family-hash match.
5. Then calculate weighted similarity across structured features.
6. Create a new Pattern when no candidate reaches the automatic threshold.

Initial similarity weights:

| Feature | Weight |
| --- | ---: |
| Error templates or exception identity | 35 |
| Stable code locations | 25 |
| Components or dependencies | 15 |
| Symptom categories | 15 |
| Normalized title tokens | 10 |

Initial decision thresholds:

- `80-100`: automatically associate with the best Pattern.
- `60-79`: expose as a possible historical match, but do not automatically associate.
- Below `60`: create a new Pattern.

Exact-hash matches have a score of 100. Family-hash equality alone must not bypass a conflicting exact symptom when adequate Crumb data exists.

Resolve equal-score ambiguity deterministically using, in order:

1. Exact-hash match.
2. Highest similarity.
3. Most recent occurrence.
4. Stable Pattern ID ordering.

Make weights, thresholds, candidate lookback, and maximum candidate count configurable under the existing `Panko` configuration section with validated safe defaults.

### 6. Case File workflow

Integrate Signature generation into `CaseFileBuilder`.

Required sequence:

1. Load the Case and its resolved Recipe.
2. Build and persist a provisional Signature.
3. Retrieve possible provisional historical matches for early display, without making a final cross-Case identity decision when collection remains enabled.
4. Collect Crumbs through the configured Crumb sources.
5. Combine newly collected Crumbs with Crumbs already present in the previous Case File.
6. Build and persist the final Signature from the complete accumulated Crumb set.
7. Match or create the authoritative Pattern transactionally.
8. Add Pattern and recurrence context to Case File composition.
9. Save and publish the Case File through existing SignalR and Slack flows.

If Crumb collection is disabled, use the provisional Signature as the authoritative Signature and mark its lower completeness in the Case File.

Signature generation failure must not prevent the main Case File from being saved. Record Signature generation as unavailable with a bounded diagnostic, log the exception, and preserve the existing durable retry semantics where safe.

Repeated Case File builds must be idempotent: they must update the same occurrence rather than incrementing the occurrence count repeatedly.

### 7. Pattern lifecycle

Support these states:

- `new`: first recorded occurrence.
- `ongoing`: recurrence while the Pattern is unresolved or after the first occurrence has been reviewed.
- `resolved`: no grouped Case remains active.
- `regressed`: a new occurrence matches a previously resolved Pattern.
- `escalating`: occurrence rate crosses a configured deterministic threshold.

For the initial implementation, escalation may use a simple rule such as at least three distinct Cases within seven days. Do not implement statistical forecasting in this goal.

PagerDuty resolution handling must update the associated occurrence. A Pattern becomes resolved only when no occurrence in that Pattern remains active. A later matching Case transitions it to regressed.

Lifecycle updates must be idempotent for duplicate PagerDuty webhook events.

### 8. Case File and API contract

Extend `CaseFile` with Signature and Pattern data containing:

- Human-readable Pattern key.
- Algorithm version and Signature stage.
- Pattern ID.
- Lifecycle state.
- Match type and score.
- Matched-feature explanation.
- Occurrence count.
- First-seen and last-seen timestamps.
- A bounded list of recent occurrences.
- Possible historical matches that did not reach the automatic threshold.
- Availability/completeness status.

Do not expose the raw normalized Crumb corpus. Expose only bounded, responder-readable match explanations.

Existing stored Case Files without the new fields must continue to deserialize and render.

### 9. Human-readable Pattern key

Generate a stable display key derived from safe normalized attributes and a short hash suffix, for example:

```text
PAYMENTS-CHECKOUT-4F19
```

The display key is not the database identity and must remain unique enough for responder communication. Raw SHA-256 hashes should not be the primary UI identity.

### 10. Web UI

Update the TypeScript Case File contract and React UI to show:

- New, recurring, regressed, escalating, or resolved badge.
- Human-readable Pattern key.
- Automatic match percentage and match type.
- A short explanation of the matched stable features.
- Occurrence count plus first and last seen.
- Recent occurrence history, including state and link to retained Case Files.
- Possible related Cases separately from authoritative Pattern history.
- A clear unavailable or provisional state while Signature generation is incomplete.

The UI must remain usable when all new fields are absent.

Use existing visual language and responsive layout. Do not introduce a new component library.

### 11. Slack output

Update the Slack Case File when Signature data is available. Keep it concise:

```text
Regressed Pattern PAYMENTS-CHECKOUT-4F19
90% match · 4 occurrences · last seen 14 June
Matched on provider timeout, checkout component, ProviderClient.SendAsync
```

Do not create additional Slack messages solely because a provisional Signature was replaced by a final Signature; update through the existing outbox/message flow.

### 12. Retention

Full Case File and Crumb retention currently defaults to 30 days. Pattern detection needs a longer compact history.

Use the validated `SignatureRetentionDays` option, which defaults to 365 days. Retain only the bounded normalized Signature, Pattern metadata, and minimal occurrence summary after the full Case File and Crumbs expire.

Do not retain raw excerpts or complete Case Files beyond their existing retention period merely to support Signature generation.

Update retention logic so:

- Full Case, Case File, and Crumb data follows the existing retention setting.
- Compact Signature/Pattern history follows Signature retention.
- Expired Patterns without retained occurrences are safely removed.

### 13. Observability

Add structured logs for:

- Signature stage and algorithm version.
- Exact, family, similarity, or new-Pattern decision.
- Selected Pattern and similarity score.
- Candidate count.
- Lifecycle transitions.
- Normalization or persistence failures without logging sensitive normalized content.

Do not log full Crumbs, excerpts, secrets, or raw normalized templates.

## Non-functional requirements

- All matching is deterministic and explainable.
- No LLM output affects hashes, matching, grouping, or lifecycle state.
- Candidate lookup and comparison are bounded.
- Signature generation adds no unbounded database scan.
- Processing is safe under concurrent worker execution.
- Existing webhook idempotency and Case File optimistic concurrency remain intact.
- Existing demo mode continues to run without requiring PostgreSQL Signature persistence; provide representative demo Signature data or a safe no-op implementation.
- Signature and Pattern fields are bounded and explicit in the canonical JSON contract.
- All timestamps use UTC.
- All string comparisons used for identity are ordinal after normalization.

## Security and privacy requirements

- Reuse configured redaction rules where possible.
- Never persist authentication headers, credentials, tokens, passwords, or connection-string secrets in Signature features.
- Treat all Crumb-source output as untrusted input.
- Bound every stored string and collection.
- Do not render normalized content as HTML.
- Do not expose internal database IDs where a safe public Pattern key is sufficient.

## Out of scope

- LLM-driven Case identity or clustering.
- Vector embeddings or external vector databases.
- Statistical anomaly forecasting.
- Automatic merging across different services.
- Automated code fixes or merge requests.
- A complete operator UI for manual merge and split.
- Backfilling every historical Case File in the first release.
- Changing the existing Crumb-source protocols.

Design the persistence and service boundaries so manual merge/split and controlled backfill can be added later.

## Implementation plan

### Step 1: Establish tests and contracts

- Add or extend an API test project using the repository's target framework.
- Add domain contracts and validated Signature generation options.
- Write normalization and deterministic hashing tests before workflow integration.

### Step 2: Implement normalization and feature extraction

- Implement bounded normalization.
- Extract stable features from Case fields and Crumbs.
- Ensure order-independent canonicalization.
- Add security and redaction tests.

### Step 3: Implement hashing and similarity matching

- Generate versioned family and exact hashes.
- Implement weighted, explainable similarity.
- Test exact, family, ambiguous, conflicting, and below-threshold cases.

### Step 4: Add database schema and repository

- Add idempotent tables, constraints, and indexes.
- Implement transactional candidate lookup, Pattern creation, occurrence association, and lifecycle updates.
- Add PostgreSQL integration tests, including concurrent Pattern creation.

### Step 5: Integrate Case File flow

- Register Signature generation services in `Program.cs`.
- Add provisional and final Signature stages to `CaseFileBuilder`.
- Make repeated runs idempotent.
- Degrade safely when Signature generation is unavailable.

### Step 6: Integrate PagerDuty resolution and lifecycle

- Update associated occurrences when PagerDuty-originated Cases resolve or reopen.
- Implement new, ongoing, resolved, regressed, and simple escalating transitions.
- Verify duplicate webhook handling.

### Step 7: Extend Case File, SignalR, and Slack contracts

- Add optional Case File fields.
- Add Pattern context to Case File composition.
- Publish Signature/history section changes.
- Add concise Slack recurrence context.

### Step 8: Implement the client UI

- Extend TypeScript types.
- Add recurrence badge, match explanation, and occurrence history.
- Handle absent, provisional, final, and unavailable states.
- Verify responsive and dark-mode layouts.

### Step 9: Implement independent retention

- Add Signature retention configuration.
- Preserve compact history when full Crumbs expire.
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
4. The same symptom in different services does not associate with one Pattern automatically.
5. Conflicting environments do not associate with one Pattern automatically when both are known.
6. Matching error templates and code locations produce a high score.
7. A family match with conflicting exact symptoms does not receive an unjustified automatic match.
8. Changing the suspected commit does not change the Signature.
9. Repeated Case File builds do not create additional occurrences.
10. Concurrent matching Cases create or select one Pattern.
11. A resolved Pattern becomes regressed when a new matching Case arrives.
12. A Pattern remains active while any occurrence is active.
13. Three distinct occurrences within the configured window become escalating.
14. Old Case Files without Signature fields still deserialize and render.
15. Missing or failed Signature generation does not prevent a Case File from being produced.
16. Sensitive values do not appear in stored features, logs, API responses, or Slack output.
17. Full Crumbs can expire while compact Pattern history remains available.
18. Demo mode remains functional.

## Acceptance criteria

The goal is complete only when all of the following are true:

- Similar Cases with dynamic IDs, counts, and timestamps receive the same deterministic Signature.
- Materially different Cases in the same service remain separate.
- Different services never associate with one Pattern automatically.
- Every automatic association includes an understandable matched-feature explanation.
- Exact and similarity decisions are stable across repeated runs and collection ordering.
- A resolved Pattern is labelled regressed when a matching Case returns.
- Pattern occurrence counts are idempotent and concurrency-safe.
- The API, web UI, and Slack Case File expose useful recurrence context.
- Demo mode exposes the same canonical Case File contract.
- Signature generation failures degrade gracefully without losing the main Case File.
- Compact Pattern history outlives raw Crumbs according to separate retention settings.
- No AI output participates in identity decisions.
- Automated tests cover the required scenarios and pass.
- API and client production builds pass.

## Verification

Discover and use the repository's existing build and test entry points. At minimum, verify the equivalent of:

```bash
dotnet test
dotnet build Panko.Api/Panko.Api.csproj
npm test --prefix Panko.Client
npm run build --prefix Panko.Client
```

If the client has no test script, add focused tests using the existing client toolchain or document and perform an appropriate alternative. Do not declare completion based only on compilation.

Manually or through an integration test demonstrate:

1. Two Cases whose titles and logs differ only by dynamic values grouping together.
2. A different failure in the same service remaining separate.
3. Resolution followed by recurrence producing the regressed state.
4. The match explanation appearing in the Case File UI and Slack rendering.

## Goal-mode working instructions

- Inspect all applicable `AGENTS.md` files before editing files in their scope.
- Preserve unrelated user changes in the working tree.
- Use small, reviewable changes and verify after each major phase.
- Prefer deterministic pure functions for normalization, canonicalization, and scoring.
- Keep persistence and matching behind interfaces so demo mode and future manual controls remain straightforward.
- Do not stop after scaffolding or partial implementation. Continue until the acceptance criteria and verification requirements are satisfied, or describe a concrete external blocker with supporting details.
- At completion, provide a concise summary of behaviour, files changed, schema/config additions, tests run, and any deliberately deferred out-of-scope work.
