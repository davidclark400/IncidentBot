import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
mkdirSync(here, { recursive: true });

const palette = {
  ink: "#172554",
  muted: "#475569",
  intakeFill: "#dbeafe",
  intakeStroke: "#2563eb",
  orchestrationFill: "#ede9fe",
  orchestrationStroke: "#7c3aed",
  sourceFill: "#dcfce7",
  sourceStroke: "#16a34a",
  prepFill: "#fef3c7",
  prepStroke: "#d97706",
  persistFill: "#ffe4e6",
  persistStroke: "#e11d48",
  outputFill: "#cffafe",
  outputStroke: "#0891b2",
  neutralFill: "#f8fafc",
  neutralStroke: "#64748b",
  headerFill: "#172554",
  headerStroke: "#172554",
  headerText: "#ffffff",
};

const now = 1_784_000_000_000;

function hash(input) {
  let value = 2166136261;
  for (const character of input) {
    value ^= character.charCodeAt(0);
    value = Math.imul(value, 16777619);
  }
  return value >>> 0;
}

function makeNode({
  id,
  x,
  y,
  width,
  height,
  text,
  fill = palette.neutralFill,
  stroke = palette.neutralStroke,
  textColor = palette.ink,
  fontSize = 18,
  strokeWidth = 2,
  radius = true,
}) {
  const textId = `${id}-text`;
  const shape = {
    id,
    type: "rectangle",
    x,
    y,
    width,
    height,
    angle: 0,
    strokeColor: stroke,
    backgroundColor: fill,
    fillStyle: "solid",
    strokeWidth,
    strokeStyle: "solid",
    roughness: 0,
    opacity: 100,
    groupIds: [],
    frameId: null,
    roundness: radius ? { type: 3 } : null,
    seed: hash(id),
    version: 1,
    versionNonce: hash(`${id}-version`),
    isDeleted: false,
    boundElements: [{ id: textId, type: "text" }],
    updated: now,
    link: null,
    locked: false,
  };
  const lines = text.split("\n");
  const lineHeight = 1.25;
  const textHeight = lines.length * fontSize * lineHeight;
  const textElement = {
    id: textId,
    type: "text",
    x: x + 12,
    y: y + (height - textHeight) / 2,
    width: width - 24,
    height: textHeight,
    angle: 0,
    strokeColor: textColor,
    backgroundColor: "transparent",
    fillStyle: "solid",
    strokeWidth: 1,
    strokeStyle: "solid",
    roughness: 0,
    opacity: 100,
    groupIds: [],
    frameId: null,
    roundness: null,
    seed: hash(textId),
    version: 1,
    versionNonce: hash(`${textId}-version`),
    isDeleted: false,
    boundElements: null,
    updated: now,
    link: null,
    locked: false,
    fontSize,
    fontFamily: 2,
    text,
    rawText: text,
    textAlign: "center",
    verticalAlign: "middle",
    containerId: id,
    originalText: text,
    autoResize: false,
    lineHeight,
  };
  return { shape, text: textElement };
}

function edgePoint(node, side) {
  if (side === "left") return [node.x, node.y + node.height / 2];
  if (side === "right") return [node.x + node.width, node.y + node.height / 2];
  if (side === "top") return [node.x + node.width / 2, node.y];
  return [node.x + node.width / 2, node.y + node.height];
}

function makeArrow({ id, from, to, fromSide = "right", toSide = "left", via = [] }) {
  const start = edgePoint(from, fromSide);
  const end = edgePoint(to, toSide);
  const absolute = [start, ...via, end];
  const points = absolute.map(([x, y]) => [x - start[0], y - start[1]]);
  const xs = points.map(([x]) => x);
  const ys = points.map(([, y]) => y);
  const bindsDirectly = via.length === 0;
  const arrow = {
    id,
    type: "arrow",
    x: start[0],
    y: start[1],
    width: Math.max(...xs) - Math.min(...xs),
    height: Math.max(...ys) - Math.min(...ys),
    angle: 0,
    strokeColor: palette.muted,
    backgroundColor: "transparent",
    fillStyle: "solid",
    strokeWidth: 2,
    strokeStyle: "solid",
    roughness: 0,
    opacity: 100,
    groupIds: [],
    frameId: null,
    roundness: via.length > 0 ? null : { type: 2 },
    seed: hash(id),
    version: 1,
    versionNonce: hash(`${id}-version`),
    isDeleted: false,
    boundElements: null,
    updated: now,
    link: null,
    locked: false,
    points,
    lastCommittedPoint: null,
    startBinding: bindsDirectly ? { elementId: from.id, focus: 0, gap: 1 } : null,
    endBinding: bindsDirectly ? { elementId: to.id, focus: 0, gap: 1 } : null,
    startArrowhead: null,
    endArrowhead: "arrow",
    elbowed: false,
  };
  if (bindsDirectly) {
    from.boundElements.push({ id, type: "arrow" });
    to.boundElements.push({ id, type: "arrow" });
  }
  return arrow;
}

function nodeMap(definitions) {
  const output = new Map();
  for (const definition of definitions) {
    const pair = makeNode(definition);
    output.set(definition.id, pair);
  }
  return output;
}

function diagram({ name, width, height, definitions, connections }) {
  const nodes = nodeMap(definitions);
  const arrows = connections.map(connection => makeArrow({
    ...connection,
    from: nodes.get(connection.from).shape,
    to: nodes.get(connection.to).shape,
  }));
  // Keep connectors behind containers so routed arrows stay legible without crossing labels.
  const elements = [...arrows];
  for (const pair of nodes.values()) elements.push(pair.shape, pair.text);

  const unboundText = elements.filter(element => element.type === "text" && !element.containerId);
  if (unboundText.length > 0) {
    throw new Error(`${name}: found ${unboundText.length} unbound text elements`);
  }

  const scene = {
    type: "excalidraw",
    version: 2,
    source: "https://excalidraw.com",
    elements,
    appState: {
      gridSize: null,
      viewBackgroundColor: "#ffffff",
      currentItemFontFamily: 2,
      currentItemFontSize: 18,
      currentItemRoughness: 0,
    },
    files: {},
  };

  writeFileSync(join(here, `${name}.excalidraw`), `${JSON.stringify(scene, null, 2)}\n`);
  writeFileSync(join(here, `${name}.svg`), renderSvg(width, height, nodes, arrows));
}

function escapeXml(value) {
  return value
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;");
}

function renderSvg(width, height, nodes, arrows) {
  const arrowMarkup = arrows.map(arrow => {
    const points = arrow.points
      .map(([x, y]) => `${arrow.x + x},${arrow.y + y}`)
      .join(" ");
    return `<polyline points="${points}" fill="none" stroke="${arrow.strokeColor}" stroke-width="${arrow.strokeWidth}" stroke-linejoin="round" stroke-linecap="round" marker-end="url(#arrowhead)"/>`;
  }).join("\n");

  const nodeMarkup = [...nodes.values()].map(({ shape, text }) => {
    const lines = text.text.split("\n");
    const lineHeight = text.fontSize * text.lineHeight;
    const firstBaseline = shape.y + shape.height / 2 - ((lines.length - 1) * lineHeight) / 2 + text.fontSize * 0.34;
    const tspans = lines.map((line, index) =>
      `<tspan x="${shape.x + shape.width / 2}" y="${firstBaseline + index * lineHeight}">${escapeXml(line)}</tspan>`
    ).join("");
    const radius = shape.roundness ? 14 : 0;
    return `<g>
  <rect x="${shape.x}" y="${shape.y}" width="${shape.width}" height="${shape.height}" rx="${radius}" fill="${shape.backgroundColor}" stroke="${shape.strokeColor}" stroke-width="${shape.strokeWidth}"/>
  <text font-family="Arial, Helvetica, sans-serif" font-size="${text.fontSize}" font-weight="${text.fontSize >= 25 ? 700 : 500}" fill="${text.strokeColor}" text-anchor="middle">${tspans}</text>
</g>`;
  }).join("\n");

  return `<?xml version="1.0" encoding="UTF-8"?>
<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}" role="img" style="max-width: 100%; height: auto;">
  <title>IncidentBot flow diagram</title>
  <defs>
    <marker id="arrowhead" markerWidth="10" markerHeight="7" refX="9" refY="3.5" orient="auto" markerUnits="strokeWidth">
      <polygon points="0 0, 10 3.5, 0 7" fill="${palette.muted}"/>
    </marker>
  </defs>
  <rect width="100%" height="100%" fill="#ffffff"/>
  ${arrowMarkup}
  ${nodeMarkup}
</svg>
`;
}

diagram({
  name: "incidentbot-numbered-process",
  width: 1600,
  height: 900,
  definitions: [
    { id: "numbered-header", x: 40, y: 30, width: 1520, height: 90, text: "IncidentBot process — document section map", fill: palette.headerFill, stroke: palette.headerStroke, textColor: palette.headerText, fontSize: 30 },

    { id: "section-1", x: 70, y: 190, width: 320, height: 160, text: "1. Trigger intake & scheduling\n\nValidate • dedupe • enqueue", fill: palette.intakeFill, stroke: palette.intakeStroke, fontSize: 21 },
    { id: "section-2", x: 450, y: 190, width: 320, height: 160, text: "2. Investigation orchestration\n\nLoad • fingerprint • collect", fill: palette.orchestrationFill, stroke: palette.orchestrationStroke, fontSize: 21 },
    { id: "section-3", x: 830, y: 190, width: 320, height: 160, text: "3. Evidence source searches\n\nPagerDuty • Nomad • GitLab\nGrafana • VictoriaLogs", fill: palette.sourceFill, stroke: palette.sourceStroke, fontSize: 20 },
    { id: "section-4", x: 1210, y: 190, width: 320, height: 160, text: "4. Prepare evidence for AI\n\nDedupe • rank\nBound • synthesize", fill: palette.prepFill, stroke: palette.prepStroke, fontSize: 20 },

    { id: "section-5", x: 1210, y: 520, width: 320, height: 160, text: "5. Compose report & recurrence\n\nProject • match • version", fill: palette.orchestrationFill, stroke: palette.orchestrationStroke, fontSize: 21 },
    { id: "section-6", x: 830, y: 520, width: 320, height: 160, text: "6. Commit PostgreSQL writes\n\nReport • evidence\nTimeline • outbox", fill: palette.persistFill, stroke: palette.persistStroke, fontSize: 20 },
    { id: "section-7", x: 450, y: 520, width: 320, height: 160, text: "7. Publish web & Slack output\n\nSignalR • post/update • restart", fill: palette.outputFill, stroke: palette.outputStroke, fontSize: 20 },
    { id: "section-8", x: 70, y: 520, width: 320, height: 160, text: "8. Apply failure semantics\n\nDegrade safely • retry durably", fill: palette.neutralFill, stroke: palette.neutralStroke, fontSize: 21 },

    { id: "numbered-footer", x: 220, y: 780, width: 1160, height: 70, text: "Follow the arrows; each number links conceptually to the matching section below.", fill: palette.neutralFill, stroke: palette.neutralStroke, fontSize: 19 },
  ],
  connections: [
    { id: "a-section-1-2", from: "section-1", to: "section-2" },
    { id: "a-section-2-3", from: "section-2", to: "section-3" },
    { id: "a-section-3-4", from: "section-3", to: "section-4" },
    { id: "a-section-4-5", from: "section-4", to: "section-5", fromSide: "bottom", toSide: "top" },
    { id: "a-section-5-6", from: "section-5", to: "section-6", fromSide: "left", toSide: "right" },
    { id: "a-section-6-7", from: "section-6", to: "section-7", fromSide: "left", toSide: "right" },
    { id: "a-section-7-8", from: "section-7", to: "section-8", fromSide: "left", toSide: "right" },
  ],
});

diagram({
  name: "incidentbot-trigger-to-output",
  width: 1880,
  height: 1060,
  definitions: [
    { id: "overview-header", x: 40, y: 30, width: 1800, height: 90, text: "IncidentBot: PagerDuty trigger to live report", fill: palette.headerFill, stroke: palette.headerStroke, textColor: palette.headerText, fontSize: 30 },

    { id: "event", x: 40, y: 175, width: 220, height: 145, text: "1. PagerDuty\nincident event", fill: palette.intakeFill, stroke: palette.intakeStroke, fontSize: 20 },
    { id: "gate", x: 300, y: 175, width: 270, height: 145, text: "2. Webhook gate\n≤256 KiB payload\nHMAC-SHA256 signature", fill: palette.intakeFill, stroke: palette.intakeStroke },
    { id: "parse", x: 610, y: 175, width: 270, height: 145, text: "3. Parse + profile\nservice + labels select scope\nfilter persisted labels", fill: palette.intakeFill, stroke: palette.intakeStroke },
    { id: "accept", x: 920, y: 175, width: 300, height: 145, text: "4. PostgreSQL transaction\ndedupe webhook event ID\nupsert incident state", fill: palette.persistFill, stroke: palette.persistStroke },
    { id: "schedule", x: 1260, y: 175, width: 270, height: 145, text: "5. Durable schedule\ntrigger/reopen: now, +30s, +90s\nother events: now", fill: palette.persistFill, stroke: palette.persistStroke },
    { id: "worker", x: 1570, y: 175, width: 270, height: 145, text: "6. Investigation worker\nlease work item\nper-incident run guard", fill: palette.orchestrationFill, stroke: palette.orchestrationStroke },

    { id: "load", x: 60, y: 430, width: 280, height: 160, text: "7. Load incident\nresolve current profile\nsave provisional fingerprint\nfind possible recurrence", fill: palette.orchestrationFill, stroke: palette.orchestrationStroke },
    { id: "initial", x: 380, y: 430, width: 280, height: 160, text: "8. Initial report\nstatus: collecting\nsources: pending\nSignalR + Slack outbox", fill: palette.outputFill, stroke: palette.outputStroke },
    { id: "collect", x: 700, y: 430, width: 320, height: 160, text: "9. Parallel collection\nPagerDuty • Nomad • GitLab\nGrafana • VictoriaLogs\nsource failures stay isolated", fill: palette.sourceFill, stroke: palette.sourceStroke },
    { id: "digest", x: 1060, y: 430, width: 320, height: 160, text: "10. Prepare AI input\ndedupe + relevance ranking\nsource-diverse ordering\n24k-character bounded digest", fill: palette.prepFill, stroke: palette.prepStroke },
    { id: "llm", x: 1420, y: 430, width: 360, height: 160, text: "11. LiteLLM synthesis\nstrict JSON schema + exact citations\ntimeout or invalid output →\ndeterministic report continues", fill: palette.prepFill, stroke: palette.prepStroke },

    { id: "final-recurrence", x: 60, y: 725, width: 320, height: 165, text: "12. Final recurrence\ndeterministic evidence fingerprint\nmatch or create problem group\nAI output is never identity input", fill: palette.orchestrationFill, stroke: palette.orchestrationStroke },
    { id: "compose", x: 420, y: 725, width: 320, height: 165, text: "13. Compose report\nevidence + timeline + sources\ncausal candidate sequence\nAI or explicit unavailable state", fill: palette.orchestrationFill, stroke: palette.orchestrationStroke },
    { id: "save", x: 780, y: 725, width: 360, height: 165, text: "14. Atomic report save\noptimistic version check\nreport JSON + evidence + timeline\ninsert slack.report outbox item", fill: palette.persistFill, stroke: palette.persistStroke },
    { id: "signalr", x: 1200, y: 725, width: 270, height: 165, text: "15. Web output\nSignalR version notification\nclient refetches report\n5s polling fallback", fill: palette.outputFill, stroke: palette.outputStroke },
    { id: "slack", x: 1510, y: 725, width: 300, height: 165, text: "16. Slack output\noutbox worker loads latest report\npost once, then update by ts\nretry with backoff", fill: palette.outputFill, stroke: palette.outputStroke },

    { id: "resilience", x: 190, y: 955, width: 1500, height: 70, text: "Durability rule: connector, recurrence, AI, and Slack failures degrade or retry independently; a committed deterministic report remains responder-visible.", fill: palette.neutralFill, stroke: palette.neutralStroke, fontSize: 19 },
  ],
  connections: [
    { id: "a-event-gate", from: "event", to: "gate" },
    { id: "a-gate-parse", from: "gate", to: "parse" },
    { id: "a-parse-accept", from: "parse", to: "accept" },
    { id: "a-accept-schedule", from: "accept", to: "schedule" },
    { id: "a-schedule-worker", from: "schedule", to: "worker" },
    { id: "a-worker-load", from: "worker", to: "load", fromSide: "bottom", toSide: "top", via: [[1705, 365], [200, 365]] },
    { id: "a-load-initial", from: "load", to: "initial" },
    { id: "a-initial-collect", from: "initial", to: "collect" },
    { id: "a-collect-digest", from: "collect", to: "digest" },
    { id: "a-digest-llm", from: "digest", to: "llm" },
    { id: "a-llm-recurrence", from: "llm", to: "final-recurrence", fromSide: "bottom", toSide: "top", via: [[1600, 655], [220, 655]] },
    { id: "a-recurrence-compose", from: "final-recurrence", to: "compose" },
    { id: "a-compose-save", from: "compose", to: "save" },
    { id: "a-save-signalr", from: "save", to: "signalr" },
    { id: "a-save-slack", from: "save", to: "slack", fromSide: "bottom", toSide: "bottom", via: [[960, 930], [1660, 930]] },
  ],
});

diagram({
  name: "incidentbot-source-searches",
  width: 1920,
  height: 1380,
  definitions: [
    { id: "sources-header", x: 40, y: 30, width: 1840, height: 90, text: "Evidence collection: what each source searches", fill: palette.headerFill, stroke: palette.headerStroke, textColor: palette.headerText, fontSize: 30 },
    { id: "scope", x: 240, y: 160, width: 1440, height: 130, text: "Shared input: profile-scoped service + safe labels • window = triggeredAt − 30 min through now • max items/bytes • profile revision\nEach enabled connector runs concurrently using native API or configured MCP transport.", fill: palette.orchestrationFill, stroke: palette.orchestrationStroke, fontSize: 20 },

    { id: "pagerduty", x: 40, y: 360, width: 350, height: 390, text: "PagerDuty\n\nGET /incidents/{pagerdutyId}\n\nExact incident lookup, not broad search.\nCaptures created time, current status,\nseverity and incident link.\n\nFinding: incident state\nTimeline: PagerDuty event", fill: palette.sourceFill, stroke: palette.sourceStroke, fontSize: 18 },
    { id: "gitlab", x: 430, y: 330, width: 700, height: 470, text: "GitLab — per allowlisted project / branch / environment\n\n• Merged MRs updated after window start; retain created + merged events\n• Commits since/until; inspect diffs for up to 5 commits, only relevantPaths\n• Parent + child pipelines updated within the window (paginated)\n• Deployments filtered by configured environments and exact window\n• Failed pipelines first; query failed current jobs, failed retry history,\n  then canceled jobs; collapse retry families and rank earliest hard failure\n• Read bounded useful trace tails for selected failed job families\n\nProduces change, pipeline, failed-step, deployment and code-reference evidence.", fill: palette.sourceFill, stroke: palette.sourceStroke, fontSize: 17 },
    { id: "nomad", x: 1170, y: 340, width: 710, height: 450, text: "Nomad — only allowlisted namespace / job pairs\n\n1. GET job state for every selected job before larger detail calls\n2. GET allocations?all=true; retain non-running/non-complete allocations\n3. GET deployments; flag non-successful state\n4. GET evaluations; flag non-complete state\n\nRequests include configured region + namespace. The Nomad token uses\nX-Nomad-Token. Primary job state is protected from a noisy job exhausting\nthe shared byte budget.", fill: palette.sourceFill, stroke: palette.sourceStroke, fontSize: 18 },

    { id: "grafana", x: 40, y: 880, width: 600, height: 370, text: "Grafana\n\n• Build dashboard and panel links for the exact time window\n• GET annotations filtered by configured tags, from/to and item limit\n• Render safe label templates into each configured datasource query\n• POST /api/ds/query with 15s interval and max 240 data points\n• Extract numeric values; compare maximum with warningAbove\n\nProduces annotation timeline events and metric snapshot findings.", fill: palette.sourceFill, stroke: palette.sourceStroke, fontSize: 18 },
    { id: "victorialogs", x: 680, y: 850, width: 700, height: 420, text: "VictoriaLogs — scoped streams and configured LogSQL templates\n\n1. Render safe label values into each expression.\n2. Count every configured query first with /select/logsql/hits (60s step).\n3. Only positive-count queries fetch samples with selected fields,\n   ascending _time order and a bounded limit (up to 20).\n4. Deduplicate NDJSON lines, apply configured regex redaction,\n   preserve the first observed error as a timeline anchor.\n\nCounting all streams first stops one noisy stream from starving later queries.", fill: palette.sourceFill, stroke: palette.sourceStroke, fontSize: 18 },
    { id: "boundary", x: 1420, y: 850, width: 460, height: 420, text: "Shared connector boundary\n\n• Per-source timeout (1–120s)\n• Cumulative byte and item limits\n• Stable IDs + provenance\n• Relevance ranking before truncation\n• Health: complete / partial / unavailable\n\nMCP additionally validates source identity,\nallowed resources and URLs; removes secrets,\ndeduplicates and fits retained output to 90%\nof its byte budget.", fill: palette.neutralFill, stroke: palette.neutralStroke, fontSize: 18 },
  ],
  connections: [
    { id: "a-scope-pd", from: "scope", to: "pagerduty", fromSide: "bottom", toSide: "top", via: [[960, 315], [215, 315]] },
    { id: "a-scope-gl", from: "scope", to: "gitlab", fromSide: "bottom", toSide: "top", via: [[960, 315], [780, 315]] },
    { id: "a-scope-nomad", from: "scope", to: "nomad", fromSide: "bottom", toSide: "top", via: [[960, 315], [1525, 315]] },
    { id: "a-scope-grafana", from: "scope", to: "grafana", fromSide: "bottom", toSide: "top", via: [[960, 820], [340, 820]] },
    { id: "a-scope-vlogs", from: "scope", to: "victorialogs", fromSide: "bottom", toSide: "top", via: [[960, 820], [1030, 820]] },
    { id: "a-scope-boundary", from: "scope", to: "boundary", fromSide: "bottom", toSide: "top", via: [[960, 820], [1650, 820]] },
  ],
});

diagram({
  name: "incidentbot-ai-persistence-output",
  width: 1920,
  height: 1340,
  definitions: [
    { id: "ai-header", x: 40, y: 30, width: 1840, height: 90, text: "From connector results to AI, PostgreSQL, web and Slack", fill: palette.headerFill, stroke: palette.headerStroke, textColor: palette.headerText, fontSize: 30 },

    { id: "results", x: 40, y: 170, width: 340, height: 210, text: "ConnectorResult[]\n\nsource health + diagnostic\nevidence findings\ntimeline candidates\nresponder links\nduration", fill: palette.sourceFill, stroke: palette.sourceStroke, fontSize: 19 },
    { id: "compress", x: 420, y: 170, width: 360, height: 210, text: "1. Canonicalize\n\nExact source+ID dedupe.\nIf 24k digest would overflow,\ncompress only repeated VictoriaLogs\ntemplates/counts and Nomad\nallocation-failure templates.", fill: palette.prepFill, stroke: palette.prepStroke, fontSize: 18 },
    { id: "rank", x: 820, y: 170, width: 360, height: 210, text: "2. Rank + diversify\n\nsignal tier → severity → confidence\n→ first hard failure → proximity\n\nTwo fair rounds across sources before\na noisy source can expand.", fill: palette.prepFill, stroke: palette.prepStroke, fontSize: 18 },
    { id: "digest-detail", x: 1220, y: 170, width: 340, height: 210, text: "3. Build bounded digest\n\nincident fields • ≤40 references\nsource health/counts • evidence IDs\nrepresentative IDs + occurrences\n≤1000-char job excerpts\n≤8 code refs/group", fill: palette.prepFill, stroke: palette.prepStroke, fontSize: 18 },
    { id: "litellm", x: 1600, y: 170, width: 280, height: 210, text: "4. LiteLLM\n\ntemperature 0 • seed 42\nstrict JSON schema\n20s default timeout\n≤1 MiB response envelope", fill: palette.prepFill, stroke: palette.prepStroke, fontSize: 18 },

    { id: "repair", x: 40, y: 500, width: 390, height: 220, text: "5. Validate model output\n\nOnly digest evidence IDs, code refs and\nsummary reference IDs survive.\nDrop unsupported diagnoses; clamp ranks\nand strengths; cap summary at 1200 chars.\nHash reuse skips unchanged synthesis.", fill: palette.prepFill, stroke: palette.prepStroke, fontSize: 18 },
    { id: "compose-report", x: 480, y: 500, width: 390, height: 220, text: "6. Compose deterministic report\n\nmerge previous + current evidence by ID\nretain ≤500 findings, ≤250 timeline\nsource health + links + high-signal count\nbuild chronology-based candidate sequence\nattach AI complete/unavailable state", fill: palette.orchestrationFill, stroke: palette.orchestrationStroke, fontSize: 18 },
    { id: "fingerprint", x: 920, y: 500, width: 390, height: 220, text: "7. Final recurrence\n\nextract stable evidence features\ngenerate versioned deterministic fingerprint\nfind scoped historical candidates\nmatch or create problem group\nupdate lifecycle + occurrence history", fill: palette.orchestrationFill, stroke: palette.orchestrationStroke, fontSize: 18 },
    { id: "transaction", x: 1360, y: 500, width: 520, height: 220, text: "8. One PostgreSQL transaction\n\noptimistic incidents.version increment + report_json\ninsert evidence rows for that report version\ninsert ordered timeline_events rows\ninsert immediate slack.report outbox item\noptional +1 minute collecting/stuck check", fill: palette.persistFill, stroke: palette.persistStroke, fontSize: 18 },

    { id: "database", x: 40, y: 850, width: 560, height: 280, text: "PostgreSQL system of record\n\nincidents • webhook_receipts • work_items\nevidence • timeline_events • outbox\nincident_fingerprints • problem_groups\nproblem_occurrences\n\nWork lease: 2 min; exponential retry ≤60s\nOutbox lease: 1 min; exponential retry ≤300s", fill: palette.persistFill, stroke: palette.persistStroke, fontSize: 18 },
    { id: "web", x: 660, y: 850, width: 420, height: 280, text: "Web / SignalR\n\nPublish IncidentUpdated(version, sections)\nand IncidentStatusChanged.\nClient joins the incident group, ignores stale\nversions, refetches with ETag and uses 5s\npolling when disconnected or report is pending.", fill: palette.outputFill, stroke: palette.outputStroke, fontSize: 18 },
    { id: "slack-output", x: 1140, y: 820, width: 740, height: 330, text: "Slack — one durable message per incident\n\nOutbox worker loads the latest committed incident + report.\nFirst delivery: chat.postMessage; save returned timestamp. Later: chat.update.\nBlocks include service/state/agent/urgency, problem match, up to 3 diverse\ntop signals, AI summary or deterministic fallback, candidate sequence,\nsource-health icons and a live-report link. A delayed collecting check may add\na Restart agent button; Socket Mode acknowledges and queues a fresh work item.", fill: palette.outputFill, stroke: palette.outputStroke, fontSize: 18 },

    { id: "failure", x: 120, y: 1210, width: 1680, height: 90, text: "Failure behavior: connector errors become unavailable results; AI errors become synthesis=unavailable; recurrence errors become problem=unavailable.\nSlack errors stay in the outbox. None erases the committed deterministic report.", fill: palette.neutralFill, stroke: palette.neutralStroke, fontSize: 19 },
  ],
  connections: [
    { id: "a-results-compress", from: "results", to: "compress" },
    { id: "a-compress-rank", from: "compress", to: "rank" },
    { id: "a-rank-digest", from: "rank", to: "digest-detail" },
    { id: "a-digest-litellm", from: "digest-detail", to: "litellm" },
    { id: "a-litellm-repair", from: "litellm", to: "repair", fromSide: "bottom", toSide: "top", via: [[1740, 445], [235, 445]] },
    { id: "a-repair-compose", from: "repair", to: "compose-report" },
    { id: "a-compose-fingerprint", from: "compose-report", to: "fingerprint" },
    { id: "a-fingerprint-transaction", from: "fingerprint", to: "transaction" },
    { id: "a-transaction-database", from: "transaction", to: "database", fromSide: "bottom", toSide: "top", via: [[1620, 780], [320, 780]] },
    { id: "a-transaction-web", from: "transaction", to: "web", fromSide: "bottom", toSide: "top", via: [[1620, 780], [870, 780]] },
    { id: "a-database-slack", from: "database", to: "slack-output", fromSide: "bottom", toSide: "bottom", via: [[320, 1170], [1510, 1170]] },
  ],
});

console.log("Generated 4 Excalidraw scenes and 4 SVG previews; every text element is bound to its container shape.");
