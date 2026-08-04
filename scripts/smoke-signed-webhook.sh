#!/usr/bin/env bash
set -euo pipefail

base_url="${1:-http://127.0.0.1:5080}"
service_id="${2:-PSMOKE}"
access_token="${SMOKE_BEARER_TOKEN:-}"
: "${PAGERDUTY_WEBHOOK_SECRET:?Set PAGERDUTY_WEBHOOK_SECRET before running the smoke test}"

if [[ ! "$service_id" =~ ^[A-Za-z0-9._:-]+$ ]]; then
  echo "Service ID contains unsupported characters" >&2
  exit 2
fi

payload_file="$(mktemp)"
response_file="$(mktemp)"
health_file="$(mktemp)"
case_file_response="$(mktemp)"
trap 'rm -f "$payload_file" "$response_file" "$health_file" "$case_file_response"' EXIT

stamp="$(date -u +%Y%m%d%H%M%S)-$$"
event_id="evt-smoke-$stamp"
pagerduty_incident_id="PD-SMOKE-$stamp"

python3 - "$payload_file" "$event_id" "$pagerduty_incident_id" "$service_id" <<'PY'
import datetime
import json
import sys

path, event_id, pagerduty_incident_id, service_id = sys.argv[1:]
payload = {
    "event": {
        "id": event_id,
        "event_type": "incident.triggered",
        "occurred_at": datetime.datetime.now(datetime.timezone.utc).isoformat().replace("+00:00", "Z"),
        "data": {
            "id": pagerduty_incident_id,
            "type": "incident",
            "title": "Panko signed webhook smoke test",
            "urgency": "low",
            "html_url": f"https://pagerduty.invalid/incidents/{pagerduty_incident_id}",
            "service": {"id": service_id},
            "custom_details": {
                "environment": "smoke",
                "component": "panko",
                "diagnostic_noise": "must not be persisted",
                "auth_token": "must not be persisted"
            }
        }
    }
}
with open(path, "w", encoding="utf-8") as output:
    json.dump(payload, output, separators=(",", ":"))
PY

signature="$(python3 - "$payload_file" <<'PY'
import hashlib
import hmac
import os
import sys

with open(sys.argv[1], "rb") as payload:
    print(hmac.new(os.environ["PAGERDUTY_WEBHOOK_SECRET"].encode(), payload.read(), hashlib.sha256).hexdigest())
PY
)"

health_code="$(curl --silent --show-error --output "$health_file" --write-out '%{http_code}' "$base_url/health/ready")"
if [[ "$health_code" != "200" ]]; then
  echo "Readiness failed with HTTP $health_code:" >&2
  cat "$health_file" >&2
  exit 1
fi

python3 - "$health_file" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    health = json.load(source)
if health.get("status") != "ready" or not health.get("productionPreflight", {}).get("ready", False):
    raise SystemExit(f"Production preflight is not ready: {json.dumps(health, separators=(',', ':'))}")
PY

webhook_code="$(curl --silent --show-error --output "$response_file" --write-out '%{http_code}' \
  --request POST \
  --header 'Content-Type: application/json' \
  --header "X-PagerDuty-Signature: v1=$signature" \
  --data-binary "@$payload_file" \
  "$base_url/api/webhooks/pagerduty/v3")"
if [[ "$webhook_code" != "202" ]]; then
  echo "Signed webhook failed with HTTP $webhook_code:" >&2
  cat "$response_file" >&2
  exit 1
fi

case_id="$(python3 - "$response_file" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    print(json.load(source)["incidentId"])
PY
)"

if [[ -z "$access_token" ]]; then
  case_file_code="$(curl --silent --show-error --output "$case_file_response" --write-out '%{http_code}' \
    --header 'X-Forwarded-User: forged-smoke-identity' \
    "$base_url/api/cases/$case_id")"
  if [[ "$case_file_code" != "401" ]]; then
    echo "Unsigned identity-header spoof was not rejected (HTTP $case_file_code):" >&2
    cat "$case_file_response" >&2
    exit 1
  fi

  echo "Signed production webhook smoke passed"
  echo "Unsigned identity header rejected"
  echo "Case ID: $case_id"
  echo "Set SMOKE_BEARER_TOKEN to poll the protected Case File"
  exit 0
fi

for _ in $(seq 1 30); do
  case_file_code="$(curl --silent --show-error --output "$case_file_response" --write-out '%{http_code}' \
    --header "Authorization: Bearer $access_token" \
    "$base_url/api/cases/$case_id")"
  if [[ "$case_file_code" == "200" ]]; then
    case_file_status="$(python3 - "$case_file_response" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    print(json.load(source).get("status", "unknown"))
PY
)"
    if [[ "$case_file_status" == "ready" || "$case_file_status" == "degraded" || "$case_file_status" == "resolved" ]]; then
      echo "Signed production webhook smoke passed"
      echo "Case ID: $case_id"
      echo "Case File: $base_url/cases/$case_id"
      exit 0
    fi
  elif [[ "$case_file_code" != "202" ]]; then
    echo "Case File lookup failed with HTTP $case_file_code:" >&2
    cat "$case_file_response" >&2
    exit 1
  fi
  sleep 1
done

echo "Case $case_id did not reach a terminal Case File state within 30 seconds" >&2
cat "$case_file_response" >&2
exit 1
