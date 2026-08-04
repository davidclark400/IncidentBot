#!/usr/bin/env python3
"""Validate and sanitize a portable Panko service-preparation bundle."""

from __future__ import annotations

import json
import math
import re
import sys
from pathlib import Path
from typing import Any

MAX_BYTES = 1024 * 1024
MAX_TEXT = 4096
TOP_LEVEL = {
    "version",
    "status",
    "observedService",
    "sources",
    "provenance",
    "coverage",
    "serviceMetrics",
    "messaging",
    "gaps",
}
SERVICE_FIELDS = {
    "name",
    "environment",
    "workloadKind",
    "team",
    "serviceCollection",
    "existingRecipeId",
}
CAPABILITIES = {
    "case-origin",
    "deployment",
    "topology",
    "changes",
    "metrics",
    "logs",
    "messaging",
    "publication",
}
COVERAGE_STATUSES = {
    "configured-and-verified",
    "proven-not-configured",
    "configured-not-verified",
    "not-applicable",
    "blocked",
}
SOURCE_KINDS = {
    "ownership",
    "service-catalog",
    "deployment",
    "repository",
    "metric-definition",
    "log-definition",
    "messaging-definition",
    "case-origin",
    "publication",
    "live-verification",
    "other",
}
AUTHORITIES = {"identity", "ownership", "signal-definition", "live-verification"}
WORKLOAD_KINDS = {"request-driven", "worker", "contract-design-review"}
SENSITIVE_KEY = re.compile(
    r"(?:authorization|credential|password|passwd|secret|token|api[-_]?key|endpoint|base[-_]?url|raw|samples?)",
    re.IGNORECASE,
)
SENSITIVE_VALUE = re.compile(
    r"(?:https?://|bearer\s+|(?:password|passwd|secret|token|api[-_]?key)\s*[=:])",
    re.IGNORECASE,
)
SAFE_ID = re.compile(r"^[a-z0-9][a-z0-9_-]{0,127}$")
SAFE_LOCATOR = re.compile(r"^[A-Za-z0-9_./:@#+=-]{1,512}$")


def fail(message: str) -> None:
    raise ValueError(message)


def require_object(value: Any, path: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        fail(f"{path} must be an object.")
    return value


def require_list(value: Any, path: str) -> list[Any]:
    if not isinstance(value, list):
        fail(f"{path} must be an array.")
    return value


def bounded_string(value: Any, path: str, *, nullable: bool = False) -> str | None:
    if value is None and nullable:
        return None
    if not isinstance(value, str) or not value.strip() or len(value) > MAX_TEXT:
        fail(f"{path} must be a non-empty string no longer than {MAX_TEXT} characters.")
    return value


def source_refs(value: Any, path: str, source_ids: set[str]) -> list[str]:
    refs = require_list(value, path)
    if any(not isinstance(ref, str) or ref not in source_ids for ref in refs):
        fail(f"{path} contains an unknown source reference.")
    if len(refs) != len(set(refs)):
        fail(f"{path} contains duplicate source references.")
    return refs


def scan_sensitive(value: Any, path: str = "$") -> None:
    if isinstance(value, dict):
        for key, child in value.items():
            if SENSITIVE_KEY.search(str(key)):
                fail(f"{path}/{key} uses a prohibited sensitive or raw-data field name.")
            scan_sensitive(child, f"{path}/{key}")
    elif isinstance(value, list):
        for index, child in enumerate(value):
            scan_sensitive(child, f"{path}/{index}")
    elif isinstance(value, str):
        if len(value) > MAX_TEXT:
            fail(f"{path} exceeds the maximum string length.")
        if SENSITIVE_VALUE.search(value):
            fail(f"{path} appears to contain a URL, credential, or secret assignment.")


def validate_sources(value: Any) -> tuple[list[dict[str, Any]], set[str], dict[str, str]]:
    sources = require_list(value, "sources")
    ids: set[str] = set()
    authorities: dict[str, str] = {}
    for index, item in enumerate(sources):
        source = require_object(item, f"sources[{index}]")
        if set(source) != {"id", "kind", "authority", "locator", "revision"}:
            fail(f"sources[{index}] has unsupported or missing fields.")
        source_id = bounded_string(source["id"], f"sources[{index}].id")
        assert source_id is not None
        if not SAFE_ID.fullmatch(source_id) or source_id in ids:
            fail(f"sources[{index}].id must be unique and use lowercase safe characters.")
        if source["kind"] not in SOURCE_KINDS:
            fail(f"sources[{index}].kind is unsupported.")
        if source["authority"] not in AUTHORITIES:
            fail(f"sources[{index}].authority is unsupported.")
        locator = bounded_string(source["locator"], f"sources[{index}].locator")
        assert locator is not None
        if not SAFE_LOCATOR.fullmatch(locator):
            fail(f"sources[{index}].locator is not a sanitized stable locator.")
        bounded_string(source["revision"], f"sources[{index}].revision", nullable=True)
        ids.add(source_id)
        authorities[source_id] = source["authority"]
    return sources, ids, authorities


def validate_observed_service(value: Any, status: str) -> dict[str, Any]:
    service = require_object(value, "observedService")
    if set(service) != SERVICE_FIELDS:
        fail("observedService has unsupported or missing fields.")
    bounded_string(service["name"], "observedService.name")
    for field in ("environment", "team", "serviceCollection", "existingRecipeId"):
        bounded_string(service[field], f"observedService.{field}", nullable=True)
    workload = service["workloadKind"]
    if workload is not None and workload not in WORKLOAD_KINDS:
        fail("observedService.workloadKind is unsupported.")
    if status == "complete" and (
        service["environment"] is None
        or service["team"] is None
        or workload not in {"request-driven", "worker"}
    ):
        fail("A complete bundle requires environment, team, and a supported workload kind.")
    return service


def validate_provenance(value: Any, service: dict[str, Any], source_ids: set[str]) -> None:
    provenance = require_object(value, "provenance")
    for pointer, refs in provenance.items():
        if not isinstance(pointer, str) or not pointer.startswith("/") or len(pointer) > 256:
            fail("provenance keys must be bounded bundle paths.")
        if not source_refs(refs, f"provenance[{pointer}]", source_ids):
            fail(f"provenance[{pointer}] must contain at least one source reference.")
    for field, field_value in service.items():
        if field_value is not None and f"/observedService/{field}" not in provenance:
            fail(f"observedService.{field} requires provenance.")


def validate_coverage(value: Any, status: str, source_ids: set[str]) -> None:
    coverage = require_list(value, "coverage")
    seen: set[str] = set()
    for index, item in enumerate(coverage):
        entry = require_object(item, f"coverage[{index}]")
        if set(entry) != {"capability", "status", "scope", "sourceRefs", "gaps"}:
            fail(f"coverage[{index}] has unsupported or missing fields.")
        capability = entry["capability"]
        if capability not in CAPABILITIES or capability in seen:
            fail(f"coverage[{index}].capability is unsupported or duplicated.")
        disposition = entry["status"]
        if disposition not in COVERAGE_STATUSES:
            fail(f"coverage[{index}].status is unsupported.")
        require_object(entry["scope"], f"coverage[{index}].scope")
        refs = source_refs(entry["sourceRefs"], f"coverage[{index}].sourceRefs", source_ids)
        gaps = require_list(entry["gaps"], f"coverage[{index}].gaps")
        for gap_index, gap in enumerate(gaps):
            bounded_string(gap, f"coverage[{index}].gaps[{gap_index}]")
        if disposition == "blocked" and not gaps:
            fail(f"coverage[{index}] is blocked but has no actionable gap.")
        if disposition != "blocked" and not refs:
            fail(f"coverage[{index}] requires provenance for disposition {disposition}.")
        if status == "complete" and (disposition not in {"configured-and-verified", "not-applicable"} or gaps):
            fail("A complete bundle cannot contain unresolved coverage.")
        seen.add(capability)
    if seen != CAPABILITIES:
        fail("coverage must contain every required capability exactly once.")


def validate_metric_definition(
    metric: Any,
    path: str,
    source_ids: set[str],
    authorities: dict[str, str],
) -> None:
    definition = require_object(metric, path)
    required = {
        "id", "title", "role", "promQl", "datasourceUid", "unit", "dashboardRow",
        "timeReducer", "crumbMode", "requirement", "direction", "warningThreshold",
        "criticalThreshold", "provenance",
    }
    if set(definition) != required:
        fail(f"{path} has unsupported or missing fields.")
    for field in ("id", "title", "role", "promQl", "datasourceUid", "unit", "dashboardRow"):
        bounded_string(definition[field], f"{path}.{field}")
    if definition["timeReducer"] not in {"maximum", "minimum", "last", "average", "sum"}:
        fail(f"{path}.timeReducer is unsupported.")
    if definition["crumbMode"] not in {"context", "anomaly"}:
        fail(f"{path}.crumbMode is unsupported.")
    if definition["requirement"] not in {"required", "optional"}:
        fail(f"{path}.requirement is unsupported.")
    if definition["direction"] not in {"above", "below"}:
        fail(f"{path}.direction is unsupported.")
    warning = definition["warningThreshold"]
    critical = definition["criticalThreshold"]
    for name, threshold in (("warningThreshold", warning), ("criticalThreshold", critical)):
        if threshold is not None and (not isinstance(threshold, (int, float)) or isinstance(threshold, bool) or not math.isfinite(threshold)):
            fail(f"{path}.{name} must be a finite number or null.")
    if (warning is None) != (critical is None):
        fail(f"{path} must provide both thresholds or neither.")
    if definition["crumbMode"] == "anomaly" and warning is None:
        fail(f"{path} uses anomaly mode without thresholds.")
    promql = definition["promQl"]
    if "{{serviceRegex}}" not in promql or "{{environmentRegex}}" not in promql:
        fail(f"{path}.promQl must contain complete service and environment placeholders.")
    provenance = require_object(definition["provenance"], f"{path}.provenance")
    provenance_fields = {"semantics", "query", "scope", "datasource", "unit", "reducer", "thresholds"}
    if set(provenance) != provenance_fields:
        fail(f"{path}.provenance has unsupported or missing fields.")
    for field in provenance_fields:
        refs = source_refs(provenance[field], f"{path}.provenance.{field}", source_ids)
        if field != "thresholds" or warning is not None:
            if not refs:
                fail(f"{path}.provenance.{field} requires signal-definition evidence.")
        if any(authorities[ref] != "signal-definition" for ref in refs):
            fail(f"{path}.provenance.{field} must reference signal-definition sources.")


def validate_service_metrics(
    value: Any,
    source_ids: set[str],
    authorities: dict[str, str],
) -> None:
    if value is None:
        return
    metrics = require_object(value, "serviceMetrics")
    if set(metrics) != {"contract", "definitions"}:
        fail("serviceMetrics has unsupported or missing fields.")
    if metrics["contract"] not in {"request-driven-v1", "worker-v1"}:
        fail("serviceMetrics.contract is unsupported.")
    definitions = require_list(metrics["definitions"], "serviceMetrics.definitions")
    ids: set[str] = set()
    for index, metric in enumerate(definitions):
        validate_metric_definition(metric, f"serviceMetrics.definitions[{index}]", source_ids, authorities)
        metric_id = metric["id"]
        if metric_id in ids:
            fail("serviceMetrics contains duplicate metric IDs.")
        ids.add(metric_id)


def validate_messaging_metric(
    value: Any,
    path: str,
    source_ids: set[str],
    authorities: dict[str, str],
) -> None:
    metric = require_object(value, path)
    required = {
        "id", "title", "category", "promQl", "datasourceUid", "resourceScope", "unit",
        "timeReducer", "crumbMode", "requirement", "warningThreshold", "criticalThreshold",
        "direction", "dashboardRow", "provenance",
    }
    if set(metric) != required:
        fail(f"{path} has unsupported or missing fields.")
    for field in ("id", "title", "category", "promQl", "datasourceUid", "unit", "dashboardRow"):
        bounded_string(metric[field], f"{path}.{field}")
    resource_scope = metric["resourceScope"]
    if resource_scope not in {"cluster", "topic", "consumer-group"}:
        fail(f"{path}.resourceScope is unsupported.")
    if metric["timeReducer"] not in {"maximum", "minimum", "last", "average", "sum"}:
        fail(f"{path}.timeReducer is unsupported.")
    if metric["crumbMode"] not in {"context", "anomaly"}:
        fail(f"{path}.crumbMode is unsupported.")
    if metric["requirement"] not in {"required", "optional"}:
        fail(f"{path}.requirement is unsupported.")
    if metric["direction"] not in {"above", "below"}:
        fail(f"{path}.direction is unsupported.")
    warning = metric["warningThreshold"]
    critical = metric["criticalThreshold"]
    for name, threshold in (("warningThreshold", warning), ("criticalThreshold", critical)):
        if threshold is not None and (
            not isinstance(threshold, (int, float))
            or isinstance(threshold, bool)
            or not math.isfinite(threshold)
        ):
            fail(f"{path}.{name} must be a finite number or null.")
    if (warning is None) != (critical is None):
        fail(f"{path} must provide both thresholds or neither.")
    if metric["crumbMode"] == "anomaly" and warning is None:
        fail(f"{path} uses anomaly mode without thresholds.")
    promql = metric["promQl"]
    required_placeholders = {"{{clusterRegex}}"}
    if resource_scope in {"topic", "consumer-group"}:
        required_placeholders.add("{{topicRegex}}")
    if resource_scope == "consumer-group":
        required_placeholders.add("{{consumerGroupRegex}}")
    if any(placeholder not in promql for placeholder in required_placeholders):
        fail(f"{path}.promQl is missing a complete resource-scope placeholder.")
    provenance = require_object(metric["provenance"], f"{path}.provenance")
    provenance_fields = {"semantics", "query", "resources", "datasource", "unit", "reducer", "thresholds"}
    if set(provenance) != provenance_fields:
        fail(f"{path}.provenance has unsupported or missing fields.")
    for field in provenance_fields:
        refs = source_refs(provenance[field], f"{path}.provenance.{field}", source_ids)
        if field != "thresholds" or warning is not None:
            if not refs:
                fail(f"{path}.provenance.{field} requires signal-definition evidence.")
        if any(authorities[ref] != "signal-definition" for ref in refs):
            fail(f"{path}.provenance.{field} must reference signal-definition sources.")


def validate_messaging(
    value: Any,
    source_ids: set[str],
    authorities: dict[str, str],
) -> None:
    messaging = require_list(value, "messaging")
    for index, item in enumerate(messaging):
        entry = require_object(item, f"messaging[{index}]")
        if set(entry) != {"kind", "resources", "metricDefinitions", "gaps"}:
            fail(f"messaging[{index}] has unsupported or missing fields.")
        if entry["kind"] != "kafka":
            fail(f"messaging[{index}].kind is unsupported in version 1.")
        resources = require_list(entry["resources"], f"messaging[{index}].resources")
        for resource_index, resource_value in enumerate(resources):
            resource = require_object(resource_value, f"messaging[{index}].resources[{resource_index}]")
            if set(resource) != {"kind", "name", "sourceRefs"}:
                fail(f"messaging[{index}].resources[{resource_index}] has unsupported or missing fields.")
            if resource["kind"] not in {"cluster", "topic", "consumer-group"}:
                fail(f"messaging[{index}].resources[{resource_index}].kind is unsupported.")
            bounded_string(resource["name"], f"messaging[{index}].resources[{resource_index}].name")
            if not source_refs(resource["sourceRefs"], f"messaging[{index}].resources[{resource_index}].sourceRefs", source_ids):
                fail(f"messaging[{index}].resources[{resource_index}] requires provenance.")
        definitions = require_list(entry["metricDefinitions"], f"messaging[{index}].metricDefinitions")
        metric_ids: set[str] = set()
        for metric_index, metric in enumerate(definitions):
            validate_messaging_metric(
                metric,
                f"messaging[{index}].metricDefinitions[{metric_index}]",
                source_ids,
                authorities,
            )
            metric_id = metric["id"]
            if metric_id in metric_ids:
                fail(f"messaging[{index}] contains duplicate metric IDs.")
            metric_ids.add(metric_id)
        gaps = require_list(entry["gaps"], f"messaging[{index}].gaps")
        for gap_index, gap in enumerate(gaps):
            bounded_string(gap, f"messaging[{index}].gaps[{gap_index}]")


def validate_gaps(value: Any, status: str, source_ids: set[str]) -> None:
    gaps = require_list(value, "gaps")
    codes: set[str] = set()
    for index, item in enumerate(gaps):
        gap = require_object(item, f"gaps[{index}]")
        if set(gap) != {"code", "message", "sourceRefs"}:
            fail(f"gaps[{index}] has unsupported or missing fields.")
        code = bounded_string(gap["code"], f"gaps[{index}].code")
        assert code is not None
        if not SAFE_ID.fullmatch(code) or code in codes:
            fail(f"gaps[{index}].code must be a unique lowercase safe key.")
        bounded_string(gap["message"], f"gaps[{index}].message")
        source_refs(gap["sourceRefs"], f"gaps[{index}].sourceRefs", source_ids)
        codes.add(code)
    if status == "complete" and gaps:
        fail("A complete bundle cannot contain gaps.")


def validate(document: Any) -> None:
    root = require_object(document, "bundle")
    if set(root) != TOP_LEVEL:
        fail("Bundle has unsupported or missing top-level fields.")
    if root["version"] != 1:
        fail("Bundle version must be 1.")
    status = root["status"]
    if status not in {"complete", "partial", "blocked"}:
        fail("Bundle status is unsupported.")
    service = validate_observed_service(root["observedService"], status)
    _, source_ids, authorities = validate_sources(root["sources"])
    validate_provenance(root["provenance"], service, source_ids)
    validate_coverage(root["coverage"], status, source_ids)
    validate_service_metrics(root["serviceMetrics"], source_ids, authorities)
    validate_messaging(root["messaging"], source_ids, authorities)
    validate_gaps(root["gaps"], status, source_ids)
    scan_sensitive(root)


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print("usage: validate_preparation.py <bundle.json>", file=sys.stderr)
        return 2
    path = Path(argv[1])
    try:
        size = path.stat().st_size
        if size > MAX_BYTES:
            fail(f"Bundle exceeds the {MAX_BYTES}-byte limit.")
        with path.open("r", encoding="utf-8") as handle:
            document = json.load(handle)
        validate(document)
    except (OSError, json.JSONDecodeError, ValueError) as error:
        print(f"invalid Panko preparation bundle: {error}", file=sys.stderr)
        return 1
    print("Panko preparation bundle is valid.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
