---
name: panko-skill
description: Prepare an existing service for Panko Case analysis without requiring access to the Panko or application codebases. Discover the named service through available authorized operational connectors, normalize source-grounded identity, ownership, scope, telemetry, and messaging facts into a portable Panko preparation bundle, validate it locally, and report unresolved gaps. Use when asked to prepare a service for Panko casework, onboard a service to Panko, or make a service usable in Panko Cases without requiring the user to name its dashboards, logs, messaging, deployment, or topology systems.
---

# Prepare a Service for Panko

## Mission

Prepare one existing workload so Panko can analyse it during a Case without replacing its current operational systems. Accept a service name as sufficient starting intent; discover implementation sources internally.

Assume neither the Panko codebase nor the service codebase is available. Everything required to perform discovery and create the handoff lives in this skill. Treat repository access as optional evidence when a connector happens to provide it, never as a prerequisite.

## Language boundary

- **Observed service**: one responder-owned workload in one environment; the preparation target.
- **Recipe**: Panko's reviewed ownership, routing, and allowed Crumb-source configuration.
- **Crumb source**: an operational system Panko may read during a Case.
- **Crumb**: a bounded, source-grounded observation.
- **Case / Case File**: one operational issue and Panko's responder-facing result.

Interpret “prepare for Panko” as making the observed service ready for Case analysis. Do not create alerting or claim continuous monitoring.

## Discover the target

1. Start with the exact service string supplied by the user. Search only connectors already available and authorized in the current session.
2. Correlate exact returned identifiers, ownership records, deployment labels, service-catalog entries, alert routing, and telemetry scope. Do not join records by fuzzy name similarity alone.
3. Resolve one environment and one logical workload. Replicas may remain one observed service; API+worker, cron+stream, or other mixed contracts require separate preparations unless reviewed normalization proves one shared contract.
4. Keep discovery read-only and bounded to the named service. Do not enumerate an organization, datasource, repository estate, or service fleet.
5. Record unavailable connectors as verification gaps, not proof that a capability is absent. Never ask the user to enumerate source technologies.

Read [references/source-routing.md](references/source-routing.md) for evidence authority and capability-specific discovery. Apply every relevant route rather than stopping after the first useful signal.

## Create the portable handoff

Read [references/preparation-bundle.md](references/preparation-bundle.md) completely before writing output. Copy [assets/panko-service-preparation.template.json](assets/panko-service-preparation.template.json) to `<service>-panko-preparation.json`, replace the starter values, and keep the document valid JSON. If no writable workspace exists, return the completed JSON as the task artifact.

The preparation bundle is the only interface between this distributed discovery agent and central Panko onboarding. Put source-grounded facts and gaps in the bundle; do not attempt to recreate Panko's internal compiler.

Never emit or hand-author:

- a final Recipe or Recipe revision;
- a selected shared metric-pack ID;
- a generated dashboard;
- connector endpoints, credentials, headers, or raw source payloads; or
- invented selectors, queries, thresholds, resource mappings, or failure patterns.

Central Panko onboarding owns policy validation, exact pack reuse or creation, Recipe compilation, deterministic dashboard generation, and provisioning handoff.

## Validate and finish

Run the bundled validator when Python 3 is available:

```bash
python3 <skill-directory>/scripts/validate_preparation.py <service>-panko-preparation.json
```

If Python is unavailable, check the same invariants manually from the contract and say that machine validation was not run. Do not require Panko repository tools.

Preparation is complete when the bundle:

- identifies one service, environment, and logical workload or records precisely why one remains unresolved;
- assesses every relevant capability as verified, unverified, not applicable with proof, or blocked;
- contains normalized facts with stable sanitized provenance;
- distinguishes absent access from absent telemetry; and
- passes the bundled validator or carries an explicit validation gap.

Report the bundle location, what Panko can compile from it, unavailable access, and remaining blockers. Ask only for an ambiguity that cannot be resolved safely—such as environment, workload identity, team ownership, or authorization—and bundle unavoidable questions into one concise request.
