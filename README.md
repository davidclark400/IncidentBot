# Panko

Panko turns a PagerDuty incident or an authorized Slack mention into a bounded, read-only Case. It collects only reviewed resources and query templates as Crumbs, asks LiteLLM for a grounded synthesis, posts to Slack, and serves a real-time React Case File.

## What is implemented

- .NET 10 ASP.NET Core API and PostgreSQL-backed workers
- signed PagerDuty V3 webhook ingestion, deduplication, and scheduled refreshes
- native PagerDuty, Nomad, Consul, GitLab, Grafana, Kafka, and VictoriaLogs Crumb-source adapters
- remote MCP Streamable HTTP transport with fixed allowlisted tool calls
- version-controlled Recipes and safe typed query substitution
- team-scoped operations catalog and GUI hierarchy for service collections and observed services
- deterministic Crumb normalization, ranking, redaction, and token budgeting
- deterministic, versioned Signatures with explainable exact/family/similarity matching
- persistent Patterns with new, ongoing, resolved, regressed, and escalating lifecycle states
- Causal Markers linking MR authorship/merge, deployed commit, Nomad failure, and first observed log error
- immutable GitLab commit/path/line references that LiteLLM diagnoses must cite by known ID
- required OpenAI-compatible LiteLLM synthesis with graceful failure to the deterministic Case File
- transactional Slack delivery using one message per Case
- bounded Slack `app_mention` queries with a validated, canonical YAML plan and threaded response
- SignalR notifications and a live React Case route
- independent 30-day Case File and 365-day compact Pattern retention, health endpoints, and focused security/determinism tests

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

2. Export any integration credentials you need in your shell. `.env` is only loaded by the pilot Compose file; ASP.NET Core does not load it during `dotnet run`. Override deployment-specific endpoints with standard ASP.NET Core environment variables (for example, `CrumbSources__Nomad__BaseUrl`). Development disables PagerDuty signature validation and enables local open access; integrations use placeholder internal hosts until configured.

3. Replace the example service IDs, resources, and query templates in `config/recipes.yaml`.

4. Run the backend:

   ```bash
   "$HOME/.dotnet/dotnet" run --project src/Panko.Api --urls http://localhost:5073
   ```

5. Run the frontend:

   ```bash
   cd src/Panko.Client
   npm install
   npm run dev
   ```

The Vite server proxies API and SignalR traffic to port `5073`. A production frontend build is written to the API's `wwwroot`; `dotnet publish` runs `npm ci` and `npm run build` automatically.

### Auth-free onboarding and v1 testing

The default local Compose stack is deliberately auth-free and intended only for a developer's own machine:

```bash
docker compose up --build -d
```

It runs the API with `ASPNETCORE_ENVIRONMENT=Development` and `JwtIdentity__Required=false`. Panko supplies the stable local identity `local-development`, grants it every Case permission, and treats every configured canonical Recipe team as accessible. The browser, REST API, inbound MCP tools, and SignalR therefore work without an OIDC provider, bearer token, team claim, group mapping, or permission claim. The Compose port is bound to `127.0.0.1`, so this all-team local mode is not exposed to the LAN.

Recipes still require a canonical `team` because Cases and Patterns persist that ownership for later production use; open mode only removes caller-side access setup. The operations catalog shows every configured team in this mode, and Recipes without an explicit `serviceCollection` appear under that team's `uncategorized` collection. External Crumb sources still require their normal credentials if they are enabled.

The same mode applies to a local `dotnet run` through `appsettings.Development.json`. It cannot be enabled in Production: startup rejects `JwtIdentity:Required=false` outside Development, and `compose.pilot.yaml` explicitly forces authenticated access.

## Configuration

Configuration has one precedence order and each file has one job:

1. `src/Panko.Api/appsettings.json` contains shared application defaults, including every Crumb-source endpoint and transport. It also names the environment variables used for secrets; it never contains secret values.
2. `appsettings.Development.json` supplies the local PostgreSQL connection and enables development-only open access by setting `JwtIdentity:Required=false`.
3. Process environment variables override either file. ASP.NET Core maps a double underscore to a section separator, so `Panko__PublicBaseUrl` overrides `Panko:PublicBaseUrl`.
4. `config/recipes.yaml` defines each observed service's owning `team`, optional team-scoped `serviceCollection`, PagerDuty/Recipe routing, and reviewed Crumb allowlists, resources, selectors, and queries. Crumb-source URLs and transports are rejected by the Recipe schema.
5. `config/service-metric-packs.yaml` is the versioned authority for reusable observed-service PromQL, datasource UIDs, signal roles, reducers, Crumb modes, and thresholds. A Recipe supplies only its exact service/environment scope and overrides.
6. `config/kafka-metric-packs.yaml` is the versioned authority for reviewed Kafka PromQL, datasource UIDs, reducers, Crumb modes, and thresholds. Its checked-in pack uses synthetic fixture metrics and is not enabled by the payments example.

`.env` is not read by the application. It is an ignored, machine-local input used by `compose.pilot.yaml` for secrets and deployment-specific overrides of the shared application settings. The Compose file passes those values into ASP.NET Core configuration and owns pilot policy such as enabling Slack and requiring it for readiness. Start from `.env.example` for a pilot deployment. For `dotnet run`, export the same variables in the shell or inject them with your development secret manager.

Interactive HTTP, inbound MCP, and SignalR access use the same signed JWT bearer identity. Configure `JwtIdentity` with the HTTPS OIDC authority, exact issuer, and audience; tokens must be signed, unexpired, and contain `sub`. A signed `panko:team` claim, or a `groups` claim mapped through `TeamAuthorization:GroupTeamMappings`, grants access only to Cases owned by that exact persisted team. A caller may have several team grants. Full Case File, Crumb, and live-update access requires `panko:permission=case:read`; Case commands use the corresponding values (`case:create`, `case:append`, `case:rebuild`, `case:refresh-sources`, or `case:close`; `*` grants every Case permission).

The checked-in browser does not run an OIDC authorization-code flow. The pilot deployment therefore expects an OIDC-aware ingress to strip any client-supplied `Authorization` header and inject `Authorization: Bearer <signed-token>` only after authenticating the browser session. The token must have the configured Panko audience and signed team/group claims, and the ingress must inject it on the SignalR WebSocket upgrade as well as ordinary HTTP requests. A future browser OIDC integration may instead send its own bearer token; the API validates either form identically and accepts SignalR's `access_token` query parameter only on `/hubs/cases`. Unsigned identity or authorization headers are never accepted.

`TrustedProxies` controls only forwarded client IP and scheme processing. Configure the exact proxy IP addresses or CIDR networks that can connect to Panko; defaults are cleared, catch-all networks are rejected, only one forwarding hop is accepted by default, and untrusted forwarded headers are ignored.

## Operations hierarchy

The browser organizes operations as **team → service collection → observed service**. A Recipe is the observed-service boundary; a service collection groups Recipes that responders operate as one distributed system, product, or platform. `serviceCollection` is optional and defaults to `uncategorized`. Its identity is scoped to the owning team, so different teams may reuse the same collection key. Team remains the authorization boundary—selecting or naming a collection cannot widen access.

All Recipes sharing one PagerDuty service must have the same effective team and service collection. This keeps a recent PagerDuty incident unambiguous before alert-specific selectors resolve its final Recipe. See [`config/README.md`](config/README.md) for the key rules and YAML example.

The GUI supports these browse routes:

- `/` — activity across all teams authorized for the caller
- `/teams/{team}` — one team's collections and activity
- `/teams/{team}/collections/{collection}` — activity for all observed services in one collection
- `/teams/{team}/collections/{collection}/services/{recipeId}` — one observed service
- `/cases/{id}` — one Case File

For v1, the authorized catalog is loaded from `GET /api/catalog`, then the browser filters the already team-authorized recent Case and PagerDuty lists by Recipe or PagerDuty service membership. The catalog response is private and non-cacheable, deterministically ordered, and contains only teams allowed by the caller's signed team/group claims. Development open-access mode returns all configured canonical teams. It never includes the reserved `unmapped` team.

Catalog loading has a skeleton and retry state; an empty catalog explains that Recipes or access need to be configured. Unknown and inaccessible nested paths use the same generic unavailable page so the GUI does not reveal hidden teams or services. Case Files resolve non-blocking team → collection → service breadcrumbs from the catalog; if the catalog is unavailable, the Case File remains usable with a home link and raw Recipe/service identifiers.

## Prepare an existing service

Use the repository-owned Panko skill with the service and outcome only:

```text
Use $panko-skill to prepare payments-api for Panko Case monitoring.
```

The skill is self-contained and assumes the agent has neither the Panko codebase nor the service codebase. It resolves the observed-service boundary from available authorized operational sources and emits one validated, source-grounded preparation bundle. Central Panko onboarding consumes that portable bundle to apply policy, select or create reviewed contracts, compile the Recipe, and generate deterministic dashboards. The request does not need to name any source implementation.

## Observable service onboarding

Existing dashboards, alert rules, recording rules, and instrumentation remain the telemetry authority. Panko maps them into a reviewed service metric pack rather than requiring every application to expose identical metric names. The pack is compiled with a Recipe's deployment-owned service and environment, and the same exact PromQL drives runtime Crumbs and an immutable generated Grafana dashboard.

This is intentionally one logical workload per Recipe, not a universal cross-system dashboard. Distributed replicas are aggregated safely under one exact canonical service label; API+worker or other mixed-contract systems use selector-specific Recipes unless a reviewed recording rule normalizes them. Offline validation proves artifact conformance only. Live query truth and provisioning of the generated JSON remain explicit read-only verification and deployment-owner handoffs.

The onboarding skill performs a bounded, authorized read-only discovery pass and records only normalized facts in `config/observability-evidence/<recipe-id>.json`. The offline tool then makes the reuse/new-pack decision deterministically; it never connects to Grafana or another live source itself. The persisted evidence JSON identifies its Recipe with `recipeId`.

```bash
dotnet run --project tools/Panko.ServiceOnboarding -- init-evidence --recipe-id payments-production --workload-kind request-driven --service payments-api --environment production --output config/observability-evidence/payments-production.json
dotnet run --project tools/Panko.ServiceOnboarding -- assess --evidence config/observability-evidence/payments-production.json --metric-packs config/service-metric-packs.yaml --output config/observability-evidence/payments-production.assessment.json
dotnet run --project tools/Panko.ServiceOnboarding -- explain --recipes config/recipes.yaml --recipe-id payments-production --metric-packs config/service-metric-packs.yaml
dotnet run --project tools/Panko.ServiceOnboarding -- generate-dashboard --recipes config/recipes.yaml --recipe-id payments-production --metric-packs config/service-metric-packs.yaml --output config/grafana/payments-production-service.json
dotnet run --project tools/Panko.ServiceOnboarding -- validate --recipes config/recipes.yaml --recipe-id payments-production --metric-packs config/service-metric-packs.yaml --dashboard config/grafana/payments-production-service.json --evidence config/observability-evidence/payments-production.json
```

`assess` returns `reuse`, `new-pack-from-contract`, `blocked`, or `contract-design-review`; it never selects a merely similar pack. Repeat assessment and generation with `--check` in CI. The repository-owned `$onboard-observable-service` skill guides the source pass, evidence review, Recipe scoping, generation, and validation. Recipes using a service metric pack cannot also define inline `grafana.queries`; existing dashboard links and annotation tags remain available as responder context.

## Kafka application onboarding

Kafka Crumbs are read through Grafana `POST /api/ds/query`; Panko never connects to brokers or writes to Kafka, Prometheus, or Grafana. `CrumbSources:Kafka` accepts only `Mode=api`, a Grafana base URL, and a read-only credential environment-variable name. A Kafka-enabled Recipe selects one reviewed metric pack, one cluster label, at least one topic, optional consumer groups, and optional threshold overrides.

Use the offline repository tool to discover application resources, generate the Panko dashboard from the same PromQL templates used at runtime, and fail closed on unresolved or uncovered resources:

```bash
dotnet run --project tools/Panko.KafkaOnboarding -- scan --app-root ../orders --environment production --output /tmp/orders-kafka.json
dotnet run --project tools/Panko.KafkaOnboarding -- generate-dashboard --recipes config/recipes.yaml --recipe-id orders-production --metric-packs config/kafka-metric-packs.yaml --output config/grafana/orders-production-kafka.json
dotnet run --project tools/Panko.KafkaOnboarding -- validate --inventory /tmp/orders-kafka.json --recipes config/recipes.yaml --recipe-id orders-production --metric-packs config/kafka-metric-packs.yaml --dashboard config/grafana/orders-production-kafka.json
```

Repeat `generate-dashboard` with `--check` in CI. The repository-owned `$onboard-kafka-app` skill runs this workflow and stops before patching when required resources or exporter mappings remain unresolved.

## Generated API contracts

The C# records in `src/Panko.Contracts` are the source of truth for HTTP responses and SignalR notifications. Building `Panko.Api` automatically:

1. emits `src/Panko.Client/openapi/panko-openapi.json` from the typed Minimal API endpoints;
2. adds the SignalR notification records to the OpenAPI component schemas; and
3. generates TypeScript under `src/Panko.Client/src/api-client`.

Do not edit the OpenAPI document or files under `src/api-client` by hand. Change the C# contract and mapper, then build the API. The generated files are committed so frontend-only and Docker builds do not require the .NET generator stage.

On machines where .NET 10 is installed under `$HOME/.dotnet`, put that installation first on `PATH`; the build-time OpenAPI tool launches a nested `dotnet` process:

```bash
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
dotnet build src/Panko.Api/Panko.Api.csproj -p:BuildClient=false
```

To verify that committed TypeScript matches the committed OpenAPI document:

```bash
npm run contracts:check --prefix src/Panko.Client
```

CI regenerates both layers and fails if either committed artifact is stale.

### OpenAPI-to-UI impact map

`src/Panko.Client/ui-contract-map.json` maps consumed OpenAPI operations, schemas, and fields to their React components and hooks. Every consumed schema and operation has a structural digest. The map also records fields the UI intentionally ignores and why.

The API and frontend builds compare the generated OpenAPI document with this map. A contract change fails the build with the affected component names. Added fields must be assigned to a UI consumer or explicitly marked ignored; removed fields must be removed from their consumer mapping.

After reviewing and making any required UI changes, accept the new structural digests with:

```bash
npm run ui-contracts:update --prefix src/Panko.Client
```

Then run the normal contract check:

```bash
npm run contracts:check --prefix src/Panko.Client
```

To run the API and PostgreSQL together in Docker:

```bash
docker compose up --build -d
```

The API is available at `http://localhost:8080`. The Compose API service supplies the container-network PostgreSQL host (`postgres`) and waits for the database health check before starting. Use `docker compose down` to stop the stack; the database volume is retained.

This default Compose path uses the auth-free Development mode described above. Use `compose.pilot.yaml` when validating the production JWT, team, and trusted-proxy boundary.

## Database-free live demo

Demo mode uses the real Case File interface, React frontend, and SignalR hub, but replaces PostgreSQL and all external Crumb sources with a staged in-memory replay:

```bash
cd src/Panko.Client
npm ci
npm run build
cd ../..
ASPNETCORE_ENVIRONMENT=Development Demo__Enabled=true "$HOME/.dotnet/dotnet" run --project src/Panko.Api --urls http://localhost:5073
```

Open `http://localhost:5073` and select **Run live demo**. The Case File resets and adds MR creation, merge, production deployment, Nomad failure, Grafana latency, first VictoriaLogs error, and a line-cited diagnosis at the configured interval. **Replay demo** resets it from the Case page. Change `Demo__StepDelaySeconds` to control the pace.

Build the production container with:

```bash
docker build -t panko:local .
```

## Pilot machine deployment

`compose.pilot.yaml` is a production-mode, single-machine starting point. It binds the API to loopback so a trusted reverse proxy can own TLS and identity, keeps PostgreSQL off the host network, runs the API with a read-only filesystem, and uses `/health/ready` rather than a TCP-only health check.

1. Copy `.env.example` to `.env`, replace every `replace-me` secret and `.example` endpoint, set the external `Panko__PublicBaseUrl`, OIDC settings, team/group mappings, Slack channel/team mappings, and exact trusted-proxy network, then keep the file readable only by the deployment account. `env_file` passes these standard ASP.NET Core override names directly to the application; the Compose YAML does not duplicate endpoint values. Pilot policy such as signature validation, required JWT authentication, and required Slack delivery comes from `compose.pilot.yaml` rather than `.env`.
2. Replace every example service ID, channel, allowlist, resource, and query in `config/recipes.yaml`; then increment its `revision`.
3. Configure the reverse proxy to:
   - terminate TLS;
   - authenticate the browser with OIDC, remove any client-supplied `Authorization`, and inject only a correctly issued, signed Panko bearer token;
   - inject that bearer token while proxying WebSockets on `/hubs/cases`;
   - send `X-Forwarded-For` and `X-Forwarded-Proto` only from an address listed in `TrustedProxies`;
   - expose `/api/webhooks/pagerduty/v3` without interactive identity while preserving the raw request body for signature validation;
   - keep port `8080` reachable only through the proxy.
4. Validate and start the stack:

   ```bash
   docker compose -f compose.pilot.yaml config --quiet
   docker compose -f compose.pilot.yaml up --build -d
   curl --fail --show-error http://127.0.0.1:8080/health/ready
   ```

Production readiness returns HTTP `503` and lists only missing environment-variable names and configuration issues until the deployment is safe to exercise. It checks the webhook secret, LiteLLM and active Crumb-source credentials, enabled Slack credentials and team-consistent channel mappings, required JWT/OIDC settings, an explicit trusted proxy, the public URL, MCP consistency, and leftover `.example` hosts on active endpoints. Development continues to return HTTP `200` while showing the same production preflight diagnostically.

For Slack delivery and Case File rebuild actions, enable Socket Mode and interactivity, give the bot the minimum `chat:write` access needed for its target channels, and give the app-level token `connections:write`. To accept mentions, also give the bot `app_mentions:read`, subscribe it to the `app_mention` bot event, and reinstall the app after changing scopes. The checked-in [Slack app manifest](src/docs/slack-app-manifest.yaml) captures those bot settings. Use read-only, resource-scoped tokens for PagerDuty and every Crumb source.

Before accepting live traffic, take a PostgreSQL backup, record the restore command, and run one controlled signed PagerDuty incident through triggered, acknowledged, reopened, and resolved states. Verify one Slack message is updated in place, the Case File link opens through the identity proxy, all configured sources report expected health, and the Case remains available after an API container restart. The application records database schema versions automatically; take a fresh backup before deploying a newer schema version.

## PagerDuty webhook

Configure a V3 webhook for the incident lifecycle events used by your organization and point it at:

```text
POST https://panko.internal/api/webhooks/pagerduty/v3
```

Set `PAGERDUTY_WEBHOOK_SECRET` to the subscription secret. Production rejects requests without a valid `X-PagerDuty-Signature` HMAC. Webhook event IDs are persisted before returning `202 Accepted`, making retries idempotent. Payloads are limited to 256 KiB by default; only runtime query keys and labels required by the selected Recipe are retained from `custom_details`.

For a local unsigned smoke test, use `ASPNETCORE_ENVIRONMENT=Development` and send a V3 payload whose service ID and custom details match a Recipe.

## Slack mention Cases

The existing Socket Mode connection can also accept a question such as:

```text
@Panko Are payment timeouts rising in production after the latest deploy?
```

Enable the bounded example and map the exact Slack channel ID to one reviewed Recipe:

```bash
Slack__Enabled=true
Slack__PromptMentionsEnabled=true
Slack__ChannelTeams__C0123456789=payments
Slack__PromptChannelRecipes__C0123456789=payments-production
LiteLlm__QueryPlannerModel=panko-case-query-planner
```

Fix every template label the Slack planner may use inside that reviewed Recipe:

```yaml
slackPromptLabels:
  service: payments-api
  environment: production
```

Both channel mappings are authorization, not defaults. `ChannelTeams` binds the exact Slack channel ID to one canonical team; `PromptChannelRecipes` may select only a Recipe owned by that team. Use channel IDs, not `#channel-name`, in both application settings and each Recipe's `slackChannel`. External Slack Connect channels are rejected unless `Slack:AllowExternalSharedChannels` is deliberately enabled. Defaults admit at most six requests per user and thirty total requests per minute; adjust `Slack:PromptRequestsPerMinutePerUser` and `Slack:PromptRequestsPerMinute` deliberately for the workspace. Prompt admission is audited before planning or datasource access, without retaining prompt text.

For each accepted mention Panko:

1. acknowledges the Socket Mode envelope before any LLM or datasource work, deduplicates the Slack `event_id`, and places the request on a bounded in-memory queue;
2. asks the query-planner model for a strict, narrow plan containing only labels, source names, and reviewed query-template names;
3. validates that plan against the channel-bound Recipe and deterministically emits a canonical YAML audit artifact like [this example](src/docs/slack-query-plan.example.yaml);
4. runs only the selected existing Crumb sources with the normal item, byte, timeout, resource, and adaptive-window limits;
5. sends the normalized Crumb-source results through the existing Case synthesis model; and
6. posts one response in the mention's thread containing the answer and canonical YAML plan.

The model cannot author a Crumb-source URL, credential, tenant, project, namespace, datasource UID, raw PromQL/LogSQL expression, MCP tool, label scope, or collection limit. Slack label values are fixed in the Recipe's `slackPromptLabels` map and the plan must copy them exactly. The YAML is an audit artifact; execution uses the compiled, narrowed copy of the deployment-owned Recipe. PagerDuty is intentionally unavailable to ad-hoc prompts because a mention has no verified PagerDuty incident identity. MCP transports are also rejected for this path because an untrusted natural-language question must not reach a remote tool before its reads are enforced; use the native, resource-scoped adapters in the working example.

The example queue and deduplication cache are process-local and intentionally bounded. A crash after acknowledgement can lose an accepted prompt, and a restart forgets prior event IDs; use durable, attempt-limited work storage before treating this conversational path as a delivery-guaranteed production workflow.

The implementation follows Slack's current [Socket Mode](https://docs.slack.dev/apis/events-api/using-socket-mode/), [`app_mention`](https://docs.slack.dev/reference/events/app_mention/), and [`chat.postMessage`](https://docs.slack.dev/reference/methods/chat.postMessage/) contracts. The bot requires `app_mentions:read` and `chat:write`; its app-level token requires `connections:write`.

## API and live updates

- `GET /api/catalog` — caller-authorized team → service collection → observed service hierarchy; returned with `Cache-Control: private, no-store`
- `GET /api/pagerduty/incidents?since={timestamp}&until={timestamp}` — pulls up to 100 recent triggered, acknowledged, and resolved PagerDuty incidents across a maximum 30-day window
- `POST /api/pagerduty/incidents/{pagerDutyId}/trigger` — accepts the selected current PagerDuty event through the idempotent Case workflow
- `GET /api/cases/{id}` — canonical Case File, with `ETag` equal to the Case File version
- `GET /api/cases/{id}/status` — Case status and Crumb-collection progress
- `GET /api/cases/{id}/trail?offset=0&limit=100`
- `GET /api/cases/{id}/crumbs?offset=0&limit=100`
- `/hubs/cases` — SignalR hub; call `JoinCase(id)` and refetch after notifications
- `GET /health/live` and `GET /health/ready` — readiness includes PostgreSQL, Recipe loading, and production configuration preflight

On startup, the API logs the PostgreSQL connection and tests each distinct configured Crumb source. Native transports use source-specific authenticated endpoints; MCP transports initialize the server and verify that the configured tool is advertised. Failed optional-source checks are logged as warnings and do not prevent the API from starting.

SignalR transports only Case IDs, versions, status, and changed-section names. Crumbs remain in the versioned HTTP representation, so reconnecting clients recover by refetching.

The Case File exposes a Trail with categories such as `merge-request-created`, `merge-request-merged`, `deployment`, `workload-failure`, and `first-error`. These are chronological correlations, deliberately presented as a candidate sequence rather than proof of causation.

The optional Pattern section exposes a safe Pattern key, lifecycle state, match score and explanation, occurrence history, and possible related Patterns. Signatures are derived only from bounded normalized Case/Crumb fields; LiteLLM output, suspected commits, actors, resource IDs, timestamps, and metric values never participate in identity.

Signature matching uses exact matches first, then family matches and weighted similarity. Automatic matches default to 80+, possible matches to 60–79, candidate history to 365 days, and candidate queries to 100 Patterns. `SignatureRetentionDays` controls compact history independently and must be at least `RetentionDays`.

GitLab diff hunks under `relevantPaths` become immutable `CodeReference` values containing project, commit SHA, path, line range, source link, and bounded excerpt. LiteLLM receives only their generated IDs. Any diagnosis that cites an unknown Crumb or code-reference ID is discarded before persistence and display.

## MCP mode

Most Crumb sources can use an API or remote MCP implementation independently. Kafka v1 is deliberately API-only and rejects MCP mode; PagerDuty incident pull also remains native. Change an eligible source's application configuration; the Recipe continues to contain only its allowed resources:

```json
{
  "CrumbSources": {
    "Nomad": {
      "Mode": "mcp",
      "BaseUrl": "https://nomad.internal.example",
      "TimeoutSeconds": 12,
      "MaxItems": 50,
      "MaxBytes": 131072,
      "Mcp": {
        "ServerUrl": "https://mcp-gateway.internal.example/nomad",
        "ToolName": "collect_case_crumbs",
        "CredentialEnv": "NOMAD_MCP_TOKEN"
      }
    }
  }
}
```

Panko initializes a Streamable HTTP session, verifies the configured tool is advertised, and sends only the policy-selected Crumb window, limits, and Recipe allowlist. Collection starts at `Panko:CrumbWindowMinutes` (30 by default) and queries disjoint older rings up to `Panko:CrumbMaximumWindowMinutes` (240 by default) while the Crumbs remain deterministically inconclusive. For a resolved PagerDuty incident, the collection end is capped at the earlier of the current time and `resolvedAt + Panko:CrumbPostResolutionWindowMinutes` (30 minutes by default), so a historical Case cannot silently include days of post-incident data. Collection stops for a structured explicit failure, temporally close high-signal Crumbs from distinct sources, or a change preceding a recent failure signal; otherwise it records a bounded inconclusive outcome. The tool must return a JSON `CrumbSourceResult`. The LLM cannot select tools, arguments, hosts, or resources.

## Verification

```bash
"$HOME/.dotnet/dotnet" test Panko.sln
cd src/Panko.Client
npm run build
npm run lint
npm run test:e2e
```

The API suite starts an isolated PostgreSQL 17 container for Signature concurrency, Pattern lifecycle, idempotency, and retention tests, so Docker must be available when running `dotnet test`.

The Playwright suite builds the client and starts the API in database-free demo mode. Install its Chromium runtime once with `npx playwright install chromium`.

## Production configuration

- Configure LiteLLM and its required `LITELLM_API_KEY`; every collected Case attempts synthesis. Enable Slack explicitly when Slack delivery is required.
- Set `Panko:PublicBaseUrl` to the ingress URL used in Slack.
- Keep `JwtIdentity:Required=true`, require HTTPS OIDC metadata, and validate the exact issuer and audience used for Panko access tokens.
- Map signed team or directory-group claims to the canonical `team` values in Recipes, and map Slack channel IDs to the same teams.
- Configure `TrustedProxies` with only the reverse proxy addresses that actually connect to the API; never use catch-all networks.
- Store all token values in the deployment secret mechanism. `appsettings.json` contains only credential environment-variable names, and Recipe YAML contains no Crumb-source transport configuration.
- Give every Crumb source read-only, resource-scoped credentials.
- Review Recipe changes through Git and increment the Recipe revision.
- Treat `team` as durable ownership, not display metadata. New Cases and Patterns snapshot it; changing a Recipe's team never transfers existing history and causes pending work under that Recipe to fail closed.
- Treat `serviceCollection` as team-scoped browse metadata. Keep all Recipes for one PagerDuty service in the same collection; omit it only when the deliberate `uncategorized` onboarding default is appropriate.
- Back up PostgreSQL before schema upgrades and test the restore procedure before the pilot.
- Review the append-only security audit Trail for allowed and denied Case File, Crumb, Slack prompt, rebuild, and export requests.
- Use one application instance for the included in-process SignalR broadcaster. For horizontal scale, add a SignalR backplane before running multiple instances.
