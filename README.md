# Incident Bot

Incident Bot turns a PagerDuty incident into a live, read-only investigation. It collects only the resources allowlisted for that service, builds a deterministic timeline, asks LiteLLM for a bounded synthesis, updates one Slack message, and serves a real-time React report.

## What is implemented

- .NET 10 ASP.NET Core API and PostgreSQL-backed workers
- signed PagerDuty V3 webhook ingestion, deduplication, and scheduled refreshes
- native PagerDuty, Nomad, GitLab, Grafana, and VictoriaLogs connectors
- remote MCP Streamable HTTP transport with fixed allowlisted tool calls
- version-controlled investigation profiles and safe typed query substitution
- deterministic evidence normalization, ranking, redaction, and token budgeting
- deterministic, versioned incident fingerprints with explainable exact/family/similarity matching
- persistent problem groups with new, ongoing, resolved, regressed, and escalating lifecycle states
- causal event categories linking MR authorship/merge, deployed commit, Nomad failure, and first observed log error
- immutable GitLab commit/path/line references that LiteLLM diagnoses must cite by known ID
- required OpenAI-compatible LiteLLM synthesis with graceful failure to the deterministic report
- transactional Slack delivery using one message per incident
- SignalR notifications and a live React incident route
- independent 30-day report and 365-day compact recurrence retention, health endpoints, and focused security/determinism tests

## Prerequisites

- .NET SDK `10.0.301` (pinned by `global.json`)
- Node.js and npm
- PostgreSQL 15+
- Docker is optional; `compose.yaml` supplies a local PostgreSQL 17 instance

The SDK can be installed without changing the system-wide SDK:

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet"
```

## Local development

1. Start PostgreSQL:

   ```bash
   docker compose up -d postgres
   ```

2. Copy `.env.example` values into your secret manager or shell. Development disables PagerDuty signature and ingress identity requirements; integrations remain disabled or use placeholder internal hosts until configured.

3. Replace the example service IDs, URLs, resources, and query templates in `config/investigation-profiles.yaml`.

4. Run the backend:

   ```bash
   "$HOME/.dotnet/dotnet" run --project src/IncidentBot.Api --urls http://localhost:5073
   ```

5. Run the frontend:

   ```bash
   cd src/IncidentBot.Client
   npm install
   npm run dev
   ```

The Vite server proxies API and SignalR traffic to port `5073`. A production frontend build is written to the API's `wwwroot`; `dotnet publish` runs `npm ci` and `npm run build` automatically.

## Generated API contracts

The C# records in `src/IncidentBot.Contracts` are the source of truth for HTTP responses and SignalR notifications. Building `IncidentBot.Api` automatically:

1. emits `src/IncidentBot.Client/openapi/incidentbot-openapi.json` from the typed Minimal API endpoints;
2. adds the SignalR notification records to the OpenAPI component schemas; and
3. generates TypeScript under `src/IncidentBot.Client/src/api-client`.

Do not edit the OpenAPI document or files under `src/api-client` by hand. Change the C# contract and mapper, then build the API. The generated files are committed so frontend-only and Docker builds do not require the .NET generator stage.

On machines where .NET 10 is installed under `$HOME/.dotnet`, put that installation first on `PATH`; the build-time OpenAPI tool launches a nested `dotnet` process:

```bash
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
dotnet build src/IncidentBot.Api/IncidentBot.Api.csproj -p:BuildClient=false
```

To verify that committed TypeScript matches the committed OpenAPI document:

```bash
npm run contracts:check --prefix src/IncidentBot.Client
```

CI regenerates both layers and fails if either committed artifact is stale.

### OpenAPI-to-UI impact map

`src/IncidentBot.Client/ui-contract-map.json` maps consumed OpenAPI operations, schemas, and fields to their React components and hooks. Every consumed schema and operation has a structural fingerprint. The map also records fields the UI intentionally ignores and why.

The API and frontend builds compare the generated OpenAPI document with this map. A contract change fails the build with the affected component names. Added fields must be assigned to a UI consumer or explicitly marked ignored; removed fields must be removed from their consumer mapping.

After reviewing and making any required UI changes, accept the new structural fingerprints with:

```bash
npm run ui-contracts:update --prefix src/IncidentBot.Client
```

Then run the normal contract check:

```bash
npm run contracts:check --prefix src/IncidentBot.Client
```

To run the API and PostgreSQL together in Docker:

```bash
docker compose up --build -d
```

The API is available at `http://localhost:8080`. The Compose API service supplies the container-network PostgreSQL host (`postgres`) and waits for the database health check before starting. Use `docker compose down` to stop the stack; the database volume is retained.

## Database-free live demo

Demo mode uses the real report API, React frontend, and SignalR hub, but replaces PostgreSQL and all external connectors with a staged in-memory replay:

```bash
cd src/IncidentBot.Client
npm ci
npm run build
cd ../..
ASPNETCORE_ENVIRONMENT=Development Demo__Enabled=true "$HOME/.dotnet/dotnet" run --project src/IncidentBot.Api --urls http://localhost:5073
```

Open `http://localhost:5073` and select **Run live demo**. The report resets and adds MR creation, merge, production deployment, Nomad failure, Grafana latency, first VictoriaLogs error, and a line-cited diagnosis at the configured interval. **Replay demo** restarts it from the incident page. Change `Demo__StepDelaySeconds` to control the pace.

Build the production container with:

```bash
docker build -t incident-bot:local .
```

## Pilot machine deployment

`compose.pilot.yaml` is a production-mode, single-machine starting point. It binds the API to loopback so a trusted reverse proxy can own TLS and identity, keeps PostgreSQL off the host network, runs the API with a read-only filesystem, and uses `/health/ready` rather than a TCP-only health check.

1. Copy `.env.example` to `.env`, replace every `replace-me` value, set the external `IncidentBot__PublicBaseUrl`, and keep the file readable only by the deployment account.
2. Replace every example service ID, channel, host, allowlist, resource, and query in `config/investigation-profiles.yaml`; then increment its `revision`.
3. Configure the reverse proxy to:
   - terminate TLS;
   - remove any client-supplied `X-Forwarded-User` header before injecting the authenticated identity;
   - proxy WebSockets on `/hubs/incidents`;
   - expose `/api/webhooks/pagerduty/v3` without interactive identity while preserving the raw request body for signature validation;
   - keep port `8080` reachable only through the proxy.
4. Validate and start the stack:

   ```bash
   docker compose -f compose.pilot.yaml config --quiet
   docker compose -f compose.pilot.yaml up --build -d
   curl --fail --show-error http://127.0.0.1:8080/health/ready
   ```

Production readiness returns HTTP `503` and lists only missing environment-variable names and configuration issues until the deployment is safe to exercise. It checks the webhook secret, the required LiteLLM credential, enabled Slack credentials, profile connector credentials, ingress identity enforcement, the public URL, MCP consistency, and leftover `.example` connector hosts. Development continues to return HTTP `200` while showing the same production preflight diagnostically.

For Slack delivery and restart actions, enable Socket Mode and interactivity, give the bot the minimum `chat:write` access needed for its target channels, and give the app-level token `connections:write`. Use read-only, resource-scoped tokens for PagerDuty and every evidence connector.

Before accepting live traffic, take a PostgreSQL backup, record the restore command, and run one controlled signed incident through triggered, acknowledged, restarted, and resolved states. Verify one Slack message is updated in place, the report link opens through the identity proxy, all configured sources report expected health, and the incident remains available after an API container restart. The application records database schema versions automatically; take a fresh backup before deploying a newer schema version.

## PagerDuty webhook

Configure a V3 webhook for the incident lifecycle events used by your organization and point it at:

```text
POST https://incident-bot.internal/api/webhooks/pagerduty/v3
```

Set `PAGERDUTY_WEBHOOK_SECRET` to the subscription secret. Production rejects requests without a valid `X-PagerDuty-Signature` HMAC. Webhook event IDs are persisted before returning `202 Accepted`, making retries idempotent. Payloads are limited to 256 KiB by default; only runtime query keys and labels required by the selected profile are retained from `custom_details`.

For a local unsigned smoke test, use `ASPNETCORE_ENVIRONMENT=Development` and send a V3 payload whose service ID and custom details match an investigation profile.

## API and live updates

- `GET /api/incidents/{id}` — canonical report, with `ETag` equal to the report version
- `GET /api/incidents/{id}/status` — collection state
- `GET /api/incidents/{id}/timeline?offset=0&limit=100`
- `GET /api/incidents/{id}/evidence?offset=0&limit=100`
- `/hubs/incidents` — SignalR hub; call `JoinIncident(id)` and refetch after notifications
- `GET /health/live` and `GET /health/ready` — readiness includes PostgreSQL, profile loading, and production configuration preflight

On startup, the API logs the PostgreSQL connection and tests each distinct configured evidence source. Native transports use source-specific authenticated endpoints; MCP transports initialize the server and verify that the configured tool is advertised. Failed optional-source checks are logged as warnings and do not prevent the API from starting.

SignalR transports only incident IDs, versions, status, and changed-section names. Evidence remains in the versioned HTTP representation, so reconnecting clients recover by refetching.

The report exposes a `causalEvents` sequence with categories such as `merge-request-created`, `merge-request-merged`, `deployment`, `workload-failure`, and `first-error`. These are chronological correlations, deliberately presented as a candidate sequence rather than proof of causation.

The optional `problem` section exposes a safe problem key, lifecycle state, match score and explanation, occurrence history, and possible related groups. Fingerprints are derived only from bounded normalized incident/evidence fields; LiteLLM output, suspected commits, actors, resource IDs, timestamps, and metric values never participate in identity. Reports stored before this section was introduced remain compatible.

Fingerprinting uses exact matches first, then family matches and weighted similarity. Automatic matches default to 80+, possible matches to 60–79, candidate history to 365 days, and candidate queries to 100 groups. Configure these under `IncidentBot` with `FingerprintAutomaticThreshold`, `FingerprintPossibleThreshold`, `FingerprintCandidateLookbackDays`, `FingerprintMaximumCandidates`, the five `Fingerprint*Weight` settings, and `FingerprintEscalationCount`/`FingerprintEscalationWindowDays`. `FingerprintRetentionDays` controls compact history independently and must be at least `RetentionDays`.

GitLab diff hunks under `relevantPaths` become immutable `CodeReference` values containing project, commit SHA, path, line range, source link, and bounded excerpt. LiteLLM receives only their generated IDs. Any diagnosis that cites an unknown evidence or code-reference ID is discarded before persistence and display.

## MCP mode

Any source connector can use an API or remote MCP implementation independently. Change that source's transport in the profile:

```yaml
nomad:
  connector:
    mode: mcp
    timeoutSeconds: 12
    maxItems: 50
    maxBytes: 131072
    mcp:
      serverUrl: https://mcp-gateway.internal.example/nomad
      toolName: collect_incident_evidence
      credentialEnv: NOMAD_MCP_TOKEN
  region: global
  namespaces:
    - name: payments-production
      jobs: [payments-api]
```

Incident Bot initializes a Streamable HTTP session, verifies the configured tool is advertised, and sends only the fixed incident window, limits, and profile allowlist. The tool must return a JSON `ConnectorResult`. The LLM cannot select tools, arguments, hosts, or resources.

## Verification

```bash
"$HOME/.dotnet/dotnet" test IncidentBot.sln
cd src/IncidentBot.Client
npm run build
npm run lint
npm run test:e2e
```

The API suite starts an isolated PostgreSQL 17 container for fingerprint concurrency, lifecycle, idempotency, and retention tests, so Docker must be available when running `dotnet test`.

The Playwright suite builds the client and starts the API in database-free demo mode. Install its Chromium runtime once with `npx playwright install chromium`.

## Production configuration

- Configure LiteLLM and its required `LITELLM_API_KEY`; every collected investigation attempts synthesis. Enable Slack explicitly when Slack delivery is required.
- Set `IncidentBot:PublicBaseUrl` to the ingress URL used in Slack.
- Keep `IngressIdentity:Required=true` and configure the proxy to remove incoming identity headers before injecting its trusted one.
- Store all token values outside YAML; profiles contain environment-variable names only.
- Give every connector read-only, resource-scoped credentials.
- Review profile changes through Git and increment the profile revision.
- Back up PostgreSQL before schema upgrades and test the restore procedure before the pilot.
- Treat authenticated incident access as organization-wide: the MVP authenticates ingress users but does not implement team-level authorization.
- Use one application instance for the included in-process SignalR broadcaster. For horizontal scale, add a SignalR backplane before running multiple instances.
