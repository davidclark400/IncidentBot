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
"$python" "$root/scripts/verify-panko-naming.py"
docker info >/dev/null
"$dotnet" restore Panko.sln
"$dotnet" test Panko.sln --no-restore
"$dotnet" build Panko.sln --configuration Release --no-restore
"$dotnet" format Panko.sln --verify-no-changes --no-restore
"$dotnet" run --project tools/Panko.KafkaOnboarding --no-restore -- \
  generate-dashboard \
  --recipes tests/fixtures/kafka-onboarding-recipe.yaml \
  --recipe-id kafka-synthetic-fixture \
  --metric-packs config/kafka-metric-packs.yaml \
  --output tests/fixtures/kafka-onboarding-dashboard.json \
  --check
"$dotnet" run --project tools/Panko.KafkaOnboarding --no-restore -- \
  validate \
  --inventory tests/fixtures/kafka-onboarding-inventory.json \
  --recipes tests/fixtures/kafka-onboarding-recipe.yaml \
  --recipe-id kafka-synthetic-fixture \
  --metric-packs config/kafka-metric-packs.yaml \
  --dashboard tests/fixtures/kafka-onboarding-dashboard.json
"$dotnet" run --project tools/Panko.ServiceOnboarding --no-restore -- \
  assess \
  --evidence tests/fixtures/service-onboarding-evidence.json \
  --metric-packs config/service-metric-packs.yaml \
  --output tests/fixtures/service-onboarding-assessment.json \
  --check
"$dotnet" run --project tools/Panko.ServiceOnboarding --no-restore -- \
  generate-dashboard \
  --recipes config/recipes.yaml \
  --recipe-id payments-production \
  --metric-packs config/service-metric-packs.yaml \
  --output config/grafana/payments-production-service.json \
  --check
"$dotnet" run --project tools/Panko.ServiceOnboarding --no-restore -- \
  validate \
  --recipes config/recipes.yaml \
  --recipe-id payments-production \
  --metric-packs config/service-metric-packs.yaml \
  --dashboard config/grafana/payments-production-service.json \
  --evidence tests/fixtures/service-onboarding-evidence.json
"$python" "$skill_creator/scripts/quick_validate.py" skills/onboard-kafka-app
"$python" "$skill_creator/scripts/quick_validate.py" skills/onboard-observable-service
"$python" "$skill_creator/scripts/quick_validate.py" skills/panko-skill
"$python" skills/panko-skill/scripts/validate_preparation.py \
  skills/panko-skill/assets/panko-service-preparation.template.json

cd "$root/src/Panko.Client"
npm ci
npm run lint
npm run build
npm run test:e2e

cd "$root"
PANKO_ENV_FILE=.env.example \
  docker compose --env-file .env.example --file compose.pilot.yaml config --quiet
docker build --tag panko-api:verify .
"$root/scripts/smoke-local-production.sh"

echo "All Panko verification gates passed"
