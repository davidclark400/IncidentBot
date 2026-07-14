#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet="${DOTNET:-$HOME/.dotnet/dotnet}"
python="${PYTHON:-python}"
skill_creator="${SKILL_CREATOR:-${CODEX_HOME:-$HOME/.codex}/skills/.system/skill-creator}"

if [[ ! -x "$dotnet" ]]; then
  dotnet="$(command -v dotnet)"
fi

cd "$root"
"$python" "$root/scripts/verify-docker-project-copies.py"
docker info >/dev/null
"$dotnet" restore IncidentBot.sln
"$dotnet" test IncidentBot.sln --no-restore
"$dotnet" build IncidentBot.sln --configuration Release --no-restore
"$dotnet" format IncidentBot.sln --verify-no-changes --no-restore
"$dotnet" run --project tools/IncidentBot.KafkaOnboarding --no-restore -- \
  generate-dashboard \
  --profiles tests/fixtures/kafka-onboarding-profile.yaml \
  --profile-id kafka-synthetic-fixture \
  --metric-packs config/kafka-metric-packs.yaml \
  --output tests/fixtures/kafka-onboarding-dashboard.json \
  --check
"$dotnet" run --project tools/IncidentBot.KafkaOnboarding --no-restore -- \
  validate \
  --inventory tests/fixtures/kafka-onboarding-inventory.json \
  --profiles tests/fixtures/kafka-onboarding-profile.yaml \
  --profile-id kafka-synthetic-fixture \
  --metric-packs config/kafka-metric-packs.yaml \
  --dashboard tests/fixtures/kafka-onboarding-dashboard.json
"$python" "$skill_creator/scripts/quick_validate.py" skills/onboard-kafka-app

cd "$root/src/IncidentBot.Client"
npm ci
npm run lint
npm run build
npm run test:e2e

cd "$root"
INCIDENTBOT_ENV_FILE=.env.example \
  docker compose --env-file .env.example --file compose.pilot.yaml config --quiet
docker build --tag incidentbot-api:verify .
"$root/scripts/smoke-local-production.sh"

echo "All Incident Bot verification gates passed"
