#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="incidentbot-smoke"
compose=(docker compose --project-name "$project" --file "$root/compose.smoke.yaml")

cleanup() {
  "${compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
}
trap cleanup EXIT

cleanup
"${compose[@]}" up --build --detach --wait
PAGERDUTY_WEBHOOK_SECRET=local-smoke-secret \
  "$root/scripts/smoke-signed-webhook.sh" http://127.0.0.1:5080 PSMOKE

if "${compose[@]}" logs api | grep -Eiq '(^|[[:space:]])(fail|critical):|Unhandled exception'; then
  echo "Production smoke container logged an application failure" >&2
  "${compose[@]}" logs api >&2
  exit 1
fi

echo "Production container logs contain no application failures"
