# Semantic Crumb compression

`SemanticCrumbCompressor` is applied only while `LiteLlmSynthesizer` builds its bounded digest.
Crumb-source results, stored Case Files, Trails, and Crumb APIs continue to use the complete source
Crumbs. The compressor is stateless and reused directly by the digest builder rather than being
registered with dependency injection.

## Invariants

- Original `CrumbSourceResult` and `Crumb` objects are never mutated.
- Exact duplicate source/ID pairs are canonicalized before semantic grouping.
- Every group retains all original member Crumb IDs for audit and future citation validation.
- Up to three deterministic representatives retain source wording, punctuation, excerpts and URLs.
- Code references retain the Crumb ID that owned them.
- First-observed log errors and GitLab change/deployment events remain independently citable.
- Reversing Crumb-source or Crumb order produces the same groups and representatives.
- Unknown sources use the conservative `preserve` policy.

## Source policies

| Source | Compressed Crumbs | Preserved Crumbs |
| --- | --- | --- |
| VictoriaLogs | Log samples with the same query and normalized dynamic-value template; repeated query-count snapshots | `first-error` and unknown categories |
| Nomad | Workload failures with the same namespace, job, object type and normalized failure template | Healthy workload state and unknown categories |
| Grafana | Metric snapshots for the same query/template; repeated annotation templates | Unknown categories |
| GitLab | Failed/cancelled pipeline jobs with the same project, job family, stage, status, failure reason and normalized trace template | Merge requests, commits, diffs, pipelines and deployments |
| PagerDuty | Nothing beyond exact duplicate-ID canonicalization | Every PagerDuty-incident Crumb |

Dynamic normalization replaces volatile values such as timestamps, UUIDs, addresses, durations,
request IDs, hashes and standalone numbers. It does not strip punctuation from retained
representatives.

## Output contract

Each `SemanticCrumbGroup` contains:

- source, category, strategy and deterministic semantic key;
- occurrence count and first/last timestamps;
- strongest severity and confidence;
- a compact aggregate summary;
- bounded strongest/first/last representatives;
- every member Crumb ID;
- bounded code references with their owning Crumb IDs.

The synthesis serializer exposes group summaries, bounded representative IDs and code references
owned by those representatives. Diagnosis validation accepts only identifiers actually serialized
into the digest. Continue evaluating captured Cases for payload size, unsupported-claim rate,
missed-root-cause rate, citation validity, latency and model cost before tuning grouping policies.
