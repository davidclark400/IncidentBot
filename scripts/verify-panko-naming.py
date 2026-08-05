#!/usr/bin/env python3
"""Reject pre-Panko product and domain vocabulary.

Panko has no deployed legacy state. This audit therefore treats old names as
errors, not compatibility seams. The only domain-specific exception is precise
PagerDuty incident language. Standards terminology such as MCP sessions and
ASP.NET ProblemDetails, launchSettings profiles, and unrelated uses of generic
technical words are outside the Panko domain and remain valid.
"""

from __future__ import annotations

import re
from collections import Counter
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
THIS_FILE = Path(__file__).resolve()

SKIPPED_PARTS = {
    ".git",
    ".idea",
    ".playwright",
    ".turbo",
    ".vscode",
    "bin",
    "coverage",
    "dist",
    "node_modules",
    "obj",
    "playwright-report",
    "TestResults",
}

TEXT_SUFFIXES = {
    "",
    ".config",
    ".cs",
    ".cshtml",
    ".csproj",
    ".css",
    ".env",
    ".example",
    ".excalidraw",
    ".graphql",
    ".html",
    ".http",
    ".json",
    ".jsx",
    ".md",
    ".mjs",
    ".props",
    ".py",
    ".sh",
    ".sln",
    ".sql",
    ".svg",
    ".targets",
    ".toml",
    ".ts",
    ".tsx",
    ".txt",
    ".xml",
    ".yaml",
    ".yml",
}

VISIBLE_PREFIXES = (
    "README.md",
    "config/",
    "docs/",
    "skills/",
    "src/CONTEXT.md",
    "src/docs/",
    "src/Panko.Client/",
    "src/Panko.Api/wwwroot/",
    "tests/fixtures/",
)

CORE_DOMAIN_PREFIXES = (
    "src/Panko.Api/",
    "src/Panko.Contracts/",
    "src/Panko.Client/",
    "tests/Panko.Api.Tests/",
)

ONBOARDING_PREFIXES = (
    "config/observability-evidence/",
    "config/grafana/",
    "skills/onboard-kafka-app/",
    "skills/onboard-observable-service/",
    "skills/panko-skill/",
    "src/Panko.Kafka/Onboarding/",
    "src/Panko.Observability/Onboarding/",
    "tests/Panko.Kafka.Tests/",
    "tests/Panko.Observability.Tests/",
    "tools/Panko.KafkaOnboarding/",
    "tools/Panko.ServiceOnboarding/",
)

FORBIDDEN_EXACT_PATHS = {
    "PANKO_COMPATIBILITY.md",
    "config/investigation-profiles.yaml",
    "docs/semantic-evidence-compression.md",
    "src/FINGERPRINTING_GOAL.md",
    "tests/fixtures/kafka-onboarding-profile.yaml",
    "tests/fixtures/smoke-profile.yaml",
}

FORBIDDEN_PATH = re.compile(
    r"(?:^|/)(?:IncidentBot|incidentbot)(?:[./_-]|$)|"
    r"(?:^|/)(?:Incidents|InvestigationSessions|Profiles|Evidence|Fingerprinting|"
    r"Problems?|Reports?|Timeline)(?:/|$)|"
    r"(?:incident-investigation-flow|semantic-evidence-compression|"
    r"generate-incident-flow|investigation-profiles|kafka-onboarding-profile|"
    r"smoke-profile)|"
    r"(?:^|[-_.])(?:Incident|Investigation|Evidence|Fingerprint|Profile|Report|"
    r"Timeline|Problem)(?:[A-Z][A-Za-z0-9]*|[-_.])",
    flags=re.IGNORECASE,
)

OLD_PRODUCT_BRAND = re.compile(r"\bincidentbot\b", flags=re.IGNORECASE)

OLD_ROUTE = re.compile(
    r"/(?:api/)?incidents(?=/|[?#{\"'`)]|$)|"
    r"/(?:api/)?investigation-sessions(?=/|[?#{\"'`)]|$)|"
    r"/hubs/incidents(?=/|[?#{\"'`)]|$)",
    flags=re.IGNORECASE,
)

OLD_SIGNALR = re.compile(
    r"\b(?:IncidentHub|JoinIncident|LeaveIncident|IncidentUpdated|"
    r"IncidentStatusChanged|IncidentProgressUpdated|"
    r"InvestigationProgressUpdated)\b"
)

OLD_MCP_VOCABULARY = re.compile(
    r"\b[A-Za-z0-9_]*(?:Incident|Investigation|Evidence|Report|Profile|"
    r"Fingerprint|Timeline)[A-Za-z0-9_]*\b|"
    r"(?<![A-Za-z0-9])(?:incident|investigation|evidence|report|profile|"
    r"fingerprint|timeline|restart)(?![A-Za-z0-9])",
    flags=re.IGNORECASE,
)

OLD_DURABLE_VALUE = re.compile(
    r"(?P<quote>[\"'`])(?:investigate|project-session|refresh-session-sources|"
    r"analyse-session|slack\.report|append-events)(?P=quote)|"
    r"(?P<prefix>:\s*)(?:investigate|project-session|refresh-session-sources|"
    r"analyse-session|slack\.report|append-events)(?=\s*(?:#|$))",
    flags=re.IGNORECASE,
)

OLD_ACTION_OR_MODEL_VALUE = re.compile(
    r"\b(?:restart_agent|manual-restart|incident-summary|incident-query-planner)\b|"
    r"(?P<quote>[\"'`])(?:report\.access|evidence\.access|"
    r"investigation\.restart\.requested|report\.export)(?P=quote)",
    flags=re.IGNORECASE,
)

OLD_CONFIG = re.compile(
    r"\b(?:InvestigationSessions|AgentSessions|ProfilesPath|EvidenceSources|"
    r"EvidenceWindowMinutes|EvidenceMaximumWindowMinutes|"
    r"EvidenceMaximumItems|EvidenceMaximumBytes|"
    r"FingerprintRetentionDays|FingerprintAutomaticThreshold|"
    r"FingerprintPossibleThreshold|FingerprintCandidateLookbackDays|"
    r"FingerprintMaximumCandidates|FingerprintEscalationCount|"
    r"FingerprintEscalationWindowDays|FingerprintErrorTemplateWeight|"
    r"FingerprintCodeLocationWeight|FingerprintComponentWeight|"
    r"FingerprintSymptomWeight|FingerprintTitleWeight|"
    r"PromptChannelProfiles)\b|"
    r"\bConnectionStrings(?::|__)(?:IncidentBot|incidentbot)\b|"
    r"\b(?:incidentbot:team|incidentbot:permission)\b|"
    r"\bX-IncidentBot-[A-Za-z0-9-]+\b|"
    r"--profiles\b|--profile-id\b",
    flags=re.IGNORECASE,
)

OLD_RECIPE_PATH = re.compile(
    r"\b(?:investigation-profiles|kafka-onboarding-profile|smoke-profile)\.ya?ml\b",
    flags=re.IGNORECASE,
)

OLD_CONTRACT_FIELD = re.compile(
    r"\b(?:sessionId|incidentId|investigationId|investigationSessionId|"
    r"profileId|profileRevision|reportVersion|reportUrl|reportJson|"
    r"baseReportVersion|deterministicReportUsable|evidenceMode|evidenceIds|"
    r"evidenceStrength|evidenceHash|findingId|findingIds|causalEvents|"
    r"fingerprintId|fingerprintStage|problemId|problemKey|earlySignals)\b",
    flags=re.IGNORECASE,
)

OLD_CONTRACT_MEMBER = re.compile(
    r"(?P<quote>[\"'`])(?:timeline|evidence|findings|problem)(?P=quote)|"
    r"^\s*(?:timeline|evidence|findings|problem)\s*:",
    flags=re.IGNORECASE,
)

OLD_DOMAIN_SYMBOL = re.compile(
    r"\b(?:Investigation[A-Za-z0-9_]*|[A-Za-z0-9_]+Investigation[A-Za-z0-9_]*|"
    r"Incident(?:Api|Context|Contract|Hub|Intake|Page|Progression|Reader|Record|"
    r"Repository|Report|Session|Store|Summary|Update|Worker)[A-Za-z0-9_]*|"
    r"SignalRIncident[A-Za-z0-9_]*|DemoIncident[A-Za-z0-9_]*|"
    r"AdaptiveEvidence[A-Za-z0-9_]*|Evidence(?:Clarity|Collection|Finding|Mode|"
    r"Ranking|Snapshot|Source)[A-Za-z0-9_]*|SemanticEvidence[A-Za-z0-9_]*|"
    r"Timeline[A-Za-z0-9_]*|[A-Za-z0-9_]+Timeline[A-Za-z0-9_]*|"
    r"Fingerprint[A-Za-z0-9_]*|[A-Za-z0-9_]+Fingerprint[A-Za-z0-9_]*|"
    r"InvestigationProfile[A-Za-z0-9_]*|Profile(?:Document|Selector|Store|Scope|"
    r"Revision|Loader)[A-Za-z0-9_]*|KafkaProfileScope[A-Za-z0-9_]*|"
    r"Problem(?:Group|Key|Match|Occurrence|Repository)[A-Za-z0-9_]*|"
    r"IncidentReport[A-Za-z0-9_]*|InvestigationReport[A-Za-z0-9_]*|"
    r"Report(?:Composer|Json|Version|Url|Reader|Transition)[A-Za-z0-9_]*|"
    r"ReadSession[A-Za-z0-9_]*|InsertSession[A-Za-z0-9_]*|"
    r"CommitReport[A-Za-z0-9_]*|EnqueueReport[A-Za-z0-9_]*|"
    r"PersistReport[A-Za-z0-9_]*|CanRequestRestart|RestartCase[A-Za-z0-9_]*|"
    r"CaseRestart[A-Za-z0-9_]*|SlackRestart[A-Za-z0-9_]*|CaseEarlySignal|"
    r"EarlySignals|RestartAsync|RequestRestart[A-Za-z0-9_]*)\b"
)

OLD_SQL_OR_PERSISTED_NAME = re.compile(
    r"\b(?:incident_id|investigation_sessions|profile_id|report_json|"
    r"evidence_inputs|client_evidence_id|evidence_kind|supersedes_evidence_id|"
    r"evidence_source_snapshots|incident_[a-z0-9_]+_receipts|"
    r"investigation_progress|base_report_version|report_version|evidence_id|"
    r"timeline_entries|incident_fingerprints|fingerprint_stage|problem_groups|"
    r"problem_key|problem_occurrences|problem_id)\b",
    flags=re.IGNORECASE,
)

OLD_INCIDENTS_TABLE = re.compile(
    r"\b(?:alter\s+table|create\s+table(?:\s+if\s+not\s+exists)?|delete\s+from|"
    r"drop\s+table(?:\s+if\s+exists)?|from|insert\s+into|join|references|"
    r"truncate(?:\s+table)?|update)\s+(?:(?:\"?public\"?)\.)?\"?incidents\"?\b",
    flags=re.IGNORECASE,
)

COMPATIBILITY_SYMBOL = re.compile(
    r"\b(?:Legacy[A-Z][A-Za-z0-9_]*|IsLegacy|UseLegacy|AllowLegacy|"
    r"CompatibilityAlias[A-Za-z0-9_]*)\b"
)

OLD_CORE_IDENTIFIER = re.compile(
    r"\b[A-Za-z0-9_]*(?:Incident|Investigation|Evidence|Timeline|Fingerprint)"
    r"[A-Za-z0-9_]*\b"
)

OLD_NAMESPACE = re.compile(
    r"\b(?:namespace|using)\s+Panko(?:\.[A-Za-z0-9_]+)*\."
    r"(?:Connectors|Incidents|InvestigationSessions|Profiles|Evidence|"
    r"Fingerprinting|Problems?|Reports?|Timeline)\b"
)

OLD_RECIPE_VERSION = re.compile(
    r"^\s*(?:[\"']?version[\"']?\s*[:=]\s*2\b)",
    flags=re.IGNORECASE,
)

OLD_CHANGED_SECTION = re.compile(r"(?P<quote>[\"'`])sources(?P=quote)")

OLD_CODE_PATH = re.compile(
    r"^src/Panko\.Api/Connectors(?:/|$)|"
    r"^src/Panko\.Client/src/features/(?:incidents|recent-incidents)(?:/|$)",
    flags=re.IGNORECASE,
)

STALE_VISIBLE_LANGUAGE = re.compile(
    r"\bincidents?\b|\binvestigations?\b|\bevidence\b|\bprofiles?\b|"
    r"\btimeline\b|\bfingerprints?\b|"
    r"\binvestigation (?:session|profile|report|workflow|state|team|window|"
    r"attempt|progress|creation|close|runner|page|view|result)s?\b|"
    r"\binvestigation sessions?\b|\binvestigation profiles?\b|"
    r"\bincident (?:id|status|details|summary|history|page|list|view|report|"
    r"timeline|workflow|state|team|window|progress)s?\b|"
    r"\b(?:recent|active|open|current) incidents?\b|"
    r"\b(?:create|open|view|load|close|restart|rebuild) (?:this )?incident\b|"
    r"\b(?:incident|investigation) reports?\b|"
    r"\bevidence (?:finding|source|collection|window|snapshot|ranking|review|"
    r"item|digest|mode)s?\b|\bevidence findings?\b|"
    r"\bprofile (?:scope|configuration|revision|loader|selection|id)s?\b|"
    r"\bPanko profiles?\b|\bproblem (?:group|key|match|occurrence)s?\b|"
    r"\b(?:deterministic|canonical|persisted|versioned|final|initial|latest|"
    r"current|protected|terminal) reports?\b|"
    r"\brestart (?:the )?(?:agent|investigation|case)\b|"
    r"\breport_(?:file|code|status)\b|\bCrumb findings?\b|"
    r"\bConnectorResult\b|\bbot-only\b",
    flags=re.IGNORECASE,
)


def repository_paths() -> list[Path]:
    paths: list[Path] = []
    for path in ROOT.rglob("*"):
        if not path.is_file() or any(part in SKIPPED_PARTS for part in path.parts):
            continue
        paths.append(path)
    return sorted(paths)


def relative(path: Path) -> str:
    return path.relative_to(ROOT).as_posix()


def is_text(path: Path) -> bool:
    return path.suffix.lower() in TEXT_SUFFIXES


def is_visible(path: str) -> bool:
    return path == "README.md" or path.startswith(VISIBLE_PREFIXES[1:])


def is_core_domain(path: str) -> bool:
    return path.startswith(CORE_DOMAIN_PREFIXES)


def is_mcp_path(path: str) -> bool:
    lowered = path.lower()
    return "/mcp/" in lowered or Path(path).name.lower().startswith("mcp")


def is_launch_settings(path: str) -> bool:
    return path.endswith("/Properties/launchSettings.json")


def is_onboarding_evidence(path: str) -> bool:
    if path.startswith(ONBOARDING_PREFIXES):
        return True
    return path in {
        "tests/fixtures/kafka-onboarding-dashboard.json",
        "tests/fixtures/kafka-onboarding-inventory.json",
        "tests/fixtures/service-onboarding-assessment.json",
        "tests/fixtures/service-onboarding-evidence.json",
    }


def is_problem_details_context(path: str, line: str, start: int, end: int) -> bool:
    window = line[max(0, start - 80):min(len(line), end + 80)]
    lowered = window.lower()
    return "problemdetails" in lowered or "problem-details" in lowered


def is_pagerduty_context(path: str, line: str, start: int, end: int) -> bool:
    if "pagerduty" in path.lower():
        return True
    if Path(path).suffix.lower() in {".excalidraw", ".svg"} and "pagerduty" in line.lower():
        return True
    window = line[max(0, start - 320):min(len(line), end + 320)].lower()
    return "pagerduty" in window or "pager-duty" in window


def is_mcp_session_context(path: str, line: str, start: int, end: int) -> bool:
    if is_mcp_path(path):
        return True
    window = line[max(0, start - 100):min(len(line), end + 100)].lower()
    return "mcp" in window or "model context protocol" in window


def add_matches(
    failures: set[tuple[str, int, str]],
    path: str,
    line_number: int,
    line: str,
    pattern: re.Pattern[str],
    label: str,
    *,
    allow=None,
    allow_context: str | None = None,
    allow_offset: int = 0,
) -> None:
    for match in pattern.finditer(line):
        context = allow_context if allow_context is not None else line
        start = match.start() + allow_offset
        end = match.end() + allow_offset
        if allow is not None and allow(path, context, start, end):
            continue
        failures.add((path, line_number, label))


def profiles_member(line: str) -> bool:
    return bool(re.search(
        r"(?P<quote>[\"'`])profiles(?P=quote)|^\s*profiles\s*:",
        line,
        flags=re.IGNORECASE,
    ))


def path_is_semantically_allowed(path: str) -> bool:
    lowered = path.lower()
    if "problem" in lowered and "problemdetails" in lowered:
        return True
    if "evidence" in lowered and is_onboarding_evidence(path):
        return True
    return False


def is_allowed_core_identifier(path: str, line: str, start: int, end: int) -> bool:
    matched = line[start:end].lower()
    if "incident" in matched and is_pagerduty_context(path, line, start, end):
        return True
    if path == "src/Panko.Api/Options/PankoOptions.cs" and matched == "maximumrecentincidents":
        return True
    return False


def is_allowed_visible_context(path: str, line: str, start: int, end: int) -> bool:
    matched = line[start:end].lower()
    if "incident" in matched and is_pagerduty_context(path, line, start, end):
        return True
    if "evidence" in matched:
        if is_onboarding_evidence(path):
            return True
        window = line[max(0, start - 320):min(len(line), end + 320)].lower()
        return any(marker in window for marker in (
            "architectural",
            "duplicat",
            "fixture",
            "new seam",
            "onboard",
            "observability-evidence",
            "metric-definition",
            "preparation bundle",
            "service telemetry",
            "source ledger",
        ))
    if "profile" in matched:
        if is_launch_settings(path):
            return True
        window = line[max(0, start - 80):min(len(line), end + 80)].lower()
        if "launch-profile" in window or "launch profile" in window:
            return True
    return False


def main() -> int:
    failures: set[tuple[str, int, str]] = set()
    paths = repository_paths()

    for file_path in paths:
        path = relative(file_path)

        if path in FORBIDDEN_EXACT_PATHS:
            failures.add((path, 0, "legacy-named artifact"))
        if re.search(r"(?:^|/)docs/adr/0003-panko-language-with-compatible", path):
            failures.add((path, 0, "compatibility ADR is forbidden"))
        if FORBIDDEN_PATH.search(path) and not path_is_semantically_allowed(path):
            failures.add((path, 0, "old product/domain name in path"))
        if OLD_CODE_PATH.search(path):
            failures.add((path, 0, "old Panko code path"))

        if file_path.resolve() == THIS_FILE or not is_text(file_path):
            continue

        try:
            text = file_path.read_text(encoding="utf-8")
        except UnicodeDecodeError:
            continue

        lines = text.splitlines()
        for index, line in enumerate(lines):
            line_number = index + 1
            context_start = max(0, index - 8)
            context_lines = lines[context_start:min(len(lines), index + 9)]
            allow_context = "\n".join(context_lines)
            allow_offset = sum(len(value) + 1 for value in lines[context_start:index])

            add_matches(
                failures, path, line_number, line,
                OLD_PRODUCT_BRAND, "old IncidentBot product brand",
            )
            add_matches(
                failures, path, line_number, line,
                OLD_ROUTE, "old Panko HTTP route",
                allow=is_pagerduty_context,
                allow_context=allow_context,
                allow_offset=allow_offset,
            )
            add_matches(
                failures, path, line_number, line,
                OLD_SIGNALR, "old SignalR hub/method/event",
            )
            add_matches(
                failures, path, line_number, line,
                OLD_DURABLE_VALUE, "old durable work/publish value",
            )
            add_matches(
                failures, path, line_number, line,
                OLD_ACTION_OR_MODEL_VALUE, "old action, id, or model value",
            )
            add_matches(
                failures, path, line_number, line,
                OLD_CONFIG, "old config, claim, header, or CLI name",
            )
            add_matches(
                failures, path, line_number, line,
                OLD_RECIPE_PATH, "old Recipe path",
            )
            add_matches(
                failures, path, line_number, line,
                OLD_NAMESPACE, "old Panko namespace",
            )
            add_matches(
                failures, path, line_number, line,
                OLD_SQL_OR_PERSISTED_NAME, "old SQL/persisted name",
            )
            add_matches(
                failures, path, line_number, line,
                OLD_INCIDENTS_TABLE, "old incidents SQL table",
            )

            if profiles_member(line) and not is_launch_settings(path):
                failures.add((path, line_number, "old Profiles configuration member"))

            if OLD_RECIPE_VERSION.search(line) and (
                "recipe" in path.lower()
                or re.search(r"\b(?:recipes|profiles)\b", allow_context, flags=re.IGNORECASE)
            ):
                failures.add((path, line_number, "unsupported Recipe schema version 2"))

            if OLD_CHANGED_SECTION.search(line) and (
                path.endswith(("CaseFileTransitions.cs", "DemoReplay.cs"))
                or re.search(
                    r"changed[_ ]?sections?|changedSections|\.Add\(",
                    allow_context,
                    flags=re.IGNORECASE,
                )
            ):
                failures.add((path, line_number, "old changed-section name"))

            if is_core_domain(path):
                add_matches(
                    failures, path, line_number, line,
                    OLD_DOMAIN_SYMBOL, "old domain symbol",
                    allow=is_pagerduty_context,
                    allow_context=allow_context,
                    allow_offset=allow_offset,
                )
                add_matches(
                    failures, path, line_number, line,
                    COMPATIBILITY_SYMBOL, "legacy compatibility symbol",
                )
                add_matches(
                    failures, path, line_number, line,
                    OLD_CORE_IDENTIFIER, "old domain identifier",
                    allow=is_allowed_core_identifier,
                    allow_context=allow_context,
                    allow_offset=allow_offset,
                )

                for match in OLD_CONTRACT_FIELD.finditer(line):
                    value = match.group(0).lower()
                    if value == "sessionid" and is_mcp_session_context(
                        path,
                        allow_context,
                        match.start() + allow_offset,
                        match.end() + allow_offset,
                    ):
                        continue
                    failures.add((path, line_number, "old JSON/contract field"))

                for match in OLD_CONTRACT_MEMBER.finditer(line):
                    value = match.group(0).lower()
                    if "evidence" in value and is_onboarding_evidence(path):
                        continue
                    if "problem" in value and is_problem_details_context(
                        path,
                        allow_context,
                        match.start() + allow_offset,
                        match.end() + allow_offset,
                    ):
                        continue
                    failures.add((path, line_number, "old JSON/contract member"))

            if is_mcp_path(path):
                add_matches(
                    failures, path, line_number, line,
                    OLD_MCP_VOCABULARY, "old MCP tool/contract vocabulary",
                    allow=is_pagerduty_context,
                    allow_context=allow_context,
                    allow_offset=allow_offset,
                )

            if is_visible(path):
                add_matches(
                    failures, path, line_number, line,
                    STALE_VISIBLE_LANGUAGE, "stale visible Panko language",
                    allow=is_allowed_visible_context,
                    allow_context=allow_context,
                    allow_offset=allow_offset,
                )

    if failures:
        ordered = sorted(failures)
        print("Panko canonical naming audit failed:")
        for path, line_number, label in ordered:
            location = f"{path}:{line_number}" if line_number else path
            print(f"- {location}: {label}")

        counts = Counter(label for _, _, label in ordered)
        print("\nInventory by category:")
        for label, count in sorted(counts.items()):
            print(f"- {label}: {count}")
        print(
            "\nPanko is canonical-only; remove the stale name instead of adding "
            "a compatibility alias or audit exception."
        )
        return 1

    print("Panko canonical naming audit passed (no legacy aliases detected).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
