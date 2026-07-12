#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet="${DOTNET:-$HOME/.dotnet/dotnet}"

if [[ ! -x "$dotnet" ]]; then
  dotnet="$(command -v dotnet)"
fi

cd "$root"
docker info >/dev/null
"$dotnet" restore IncidentBot.sln
"$dotnet" test IncidentBot.sln --no-restore
"$dotnet" build IncidentBot.sln --configuration Release --no-restore
"$dotnet" format IncidentBot.sln --verify-no-changes --no-restore

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
