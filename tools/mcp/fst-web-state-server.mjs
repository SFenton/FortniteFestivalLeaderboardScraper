#!/usr/bin/env node

/**
 * FST Web State MCP Server — Generates JavaScript snippets for bootstrapping
 * FortniteFestivalWeb browser state via Playwright's page.evaluate().
 *
 * Tools return JS code strings that agents pass to playwright/evaluate to
 * set, read, or reset localStorage keys in the running browser session.
 *
 * No environment variables required — this server has no external dependencies.
 */

import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";

/* ── Registered test accounts ─────────────────────────────────────────── */

const TEST_ACCOUNTS = {
  sfentonx: { accountId: "195e93ef108143b2975ee46662d4d0e1", displayName: "SFentonX" },
  captainparticles: { accountId: "cb8ebb19b32c40d1a736d7f8efec17ac", displayName: "captainparticles" },
  kahnyri: { accountId: "4c2a1300df4c49a9b9d2b352d704bdf0", displayName: "kahnyri" },
};

/* ── Valid enum values ────────────────────────────────────────────────── */

const SORT_MODES = [
  "title", "artist", "year", "shop", "hasfc",
  "score", "percentage", "percentile", "stars",
  "seasonachieved", "intensity", "maxdistance",
];

const INSTRUMENTS = [
  "Solo_Guitar", "Solo_Bass", "Solo_Drums",
  "Solo_Vocals", "Solo_PeripheralGuitar", "Solo_PeripheralBass",
];

const INSTRUMENT_LABELS = {
  Solo_Guitar: "Lead", Solo_Bass: "Bass", Solo_Drums: "Drums",
  Solo_Vocals: "Vocals", Solo_PeripheralGuitar: "Pro Lead", Solo_PeripheralBass: "Pro Bass",
};

const RANK_BY_MODES = ["totalscore", "adjusted", "weighted", "fcrate", "maxscore"];

const DEFAULT_METADATA_ORDER = [
  "score", "percentage", "percentile", "stars",
  "seasonachieved", "intensity", "maxdistance",
];

const DEFAULT_INSTRUMENT_ORDER = [...INSTRUMENTS];

/* ── localStorage key reference ───────────────────────────────────────── */

const STORAGE_KEYS = {
  trackedPlayer: "fst:trackedPlayer",
  firstRun: "fst:firstRun",
  songSettings: "fst:songSettings",
  leaderboardSettings: "fst:leaderboardSettings",
  suggestionsFilter: "fst-suggestions-filter",
  songsCache: "fst_songs_cache",
};

/* ── Helpers ──────────────────────────────────────────────────────────── */

function resolvePlayer(nameOrId) {
  if (!nameOrId) return TEST_ACCOUNTS.sfentonx;
  const lower = nameOrId.toLowerCase();
  if (TEST_ACCOUNTS[lower]) return TEST_ACCOUNTS[lower];
  // Treat as raw accountId
  if (/^[0-9a-f]{32}$/i.test(nameOrId)) {
    return { accountId: nameOrId, displayName: "Unknown User" };
  }
  // Resolve by displayName match (case-insensitive)
  for (const acct of Object.values(TEST_ACCOUNTS)) {
    if (acct.displayName.toLowerCase() === lower) return acct;
  }
  return { accountId: nameOrId, displayName: nameOrId };
}

function buildSongSettings({ sortMode, sortAscending, instrument, metadataOrder, instrumentOrder, filters }) {
  const settings = {
    sortMode: sortMode || "title",
    sortAscending: sortAscending !== false,
    metadataOrder: metadataOrder || [...DEFAULT_METADATA_ORDER],
    instrumentOrder: instrumentOrder || [...DEFAULT_INSTRUMENT_ORDER],
    filters: filters || {
      missingScores: {}, missingFCs: {}, hasScores: {}, hasFCs: {},
      overThreshold: {}, seasonFilter: {}, percentileFilter: {},
      starsFilter: {}, difficultyFilter: {},
    },
    instrument: instrument || null,
  };
  return settings;
}

/* ── FRE slide registry grouped by page ────────────────────────────────── */

const FRE_SLIDES = {
  songs: [
    "songs-song-list", "songs-sort", "songs-filter", "songs-metadata",
    "songs-navigation", "songs-icons", "songs-shop-highlight", "songs-leaving-tomorrow",
  ],
  songinfo: [
    "songinfo-top-scores", "songinfo-chart", "songinfo-bar-select",
    "songinfo-view-all", "songinfo-paths", "songinfo-shop-button",
  ],
  shop: ["shop-overview", "shop-views", "shop-highlighting", "shop-leaving-tomorrow"],
  statistics: [
    "statistics-overview", "statistics-percentiles",
    "statistics-instrument-breakdown", "statistics-top-songs", "statistics-drill-down",
  ],
  rivals: ["rivals-overview", "rivals-instruments", "rivals-detail"],
  leaderboards: [
    "leaderboards-overview", "leaderboards-experimental-metrics", "leaderboards-your-rank",
  ],
  suggestions: [
    "suggestions-category-card", "suggestions-global-filter",
    "suggestions-instrument-filter", "suggestions-infinite-scroll",
  ],
  compete: ["compete-hub", "compete-leaderboards", "compete-rivals"],
  playerhistory: ["playerhistory-sort", "playerhistory-score-list"],
  metricinfo: [
    "metric-info-adjusted-how", "metric-info-adjusted-experience",
    "metric-info-adjusted-hood", "metric-info-adjusted-experimental",
    "metric-info-weighted-how", "metric-info-weighted-experience",
    "metric-info-weighted-hood", "metric-info-weighted-experimental",
    "metric-info-fcrate-how", "metric-info-fcrate-experience",
    "metric-info-fcrate-hood", "metric-info-fcrate-experimental",
    "metric-info-maxscore-how", "metric-info-maxscore-experience",
    "metric-info-maxscore-hood", "metric-info-maxscore-experimental",
  ],
};

const ALL_FRE_SLIDE_IDS = Object.values(FRE_SLIDES).flat();
const FRE_PAGE_NAMES = Object.keys(FRE_SLIDES);

function buildFirstRunClear(slideFilter) {
  const now = new Date().toISOString();
  let ids;

  if (!slideFilter || slideFilter === true) {
    // Clear all
    ids = ALL_FRE_SLIDE_IDS;
  } else if (Array.isArray(slideFilter)) {
    // Resolve: each entry can be a page name (e.g., "songs") or specific slide ID
    ids = [];
    for (const entry of slideFilter) {
      if (FRE_SLIDES[entry]) {
        ids.push(...FRE_SLIDES[entry]);
      } else {
        ids.push(entry);
      }
    }
  } else {
    ids = ALL_FRE_SLIDE_IDS;
  }

  const result = {};
  for (const id of ids) {
    result[id] = { version: 999, hash: "cleared", seenAt: now };
  }
  return result;
}

/* ── Tool definitions ─────────────────────────────────────────────────── */

const TOOLS = [
  {
    name: "web_state_bootstrap",
    description:
      "Generate a JavaScript snippet that sets FortniteFestivalWeb localStorage state. " +
      "Pass the returned JS to Playwright's page.evaluate() to configure the browser before navigating. " +
      "Default player: SFentonX. Accepts player name, sort mode, instrument filter, and more.",
    inputSchema: {
      type: "object",
      properties: {
        player: {
          type: "string",
          description:
            "Player name or accountId. Registered names: SFentonX (default), captainparticles, kahnyri. " +
            "Pass 'none' to deselect player.",
        },
        sortMode: {
          type: "string",
          enum: SORT_MODES,
          description: "Song sort mode (default: title).",
        },
        sortAscending: {
          type: "boolean",
          description: "Sort ascending (default: true).",
        },
        instrument: {
          type: "string",
          enum: [...INSTRUMENTS],
          description:
            "Instrument filter. Use label names too: Lead=Solo_Guitar, Bass=Solo_Bass, " +
            "Drums=Solo_Drums, Vocals=Solo_Vocals, Pro Lead=Solo_PeripheralGuitar, Pro Bass=Solo_PeripheralBass.",
        },
        clearFre: {
          oneOf: [
            { type: "boolean" },
            {
              type: "array",
              items: { type: "string" },
              description:
                "Array of page names and/or specific slide IDs to mark as seen. " +
                "Page names: " + FRE_PAGE_NAMES.join(", ") + ". " +
                "Example: ['songs', 'shop'] clears all songs + shop slides. " +
                "Example: ['songs-sort', 'songs-filter'] clears only those two slides.",
            },
          ],
          description:
            "Control first-run experience clearing. true (default) = clear all, false = don't clear, " +
            "array = clear specific pages or slides.",
        },
        metadataVisibility: {
          type: "object",
          description:
            "Override metadata visibility. Keys: score, percentage, percentile, stars, seasonachieved, intensity. " +
            "Values: true (show) or false (hide). Unspecified keys default to visible.",
          properties: {
            score: { type: "boolean" },
            percentage: { type: "boolean" },
            percentile: { type: "boolean" },
            stars: { type: "boolean" },
            seasonachieved: { type: "boolean" },
            intensity: { type: "boolean" },
          },
        },
        leaderboardRankBy: {
          type: "string",
          enum: RANK_BY_MODES,
          description: "Leaderboard ranking metric (default: totalscore).",
        },
      },
    },
  },
  {
    name: "web_state_describe",
    description:
      "Returns a reference of all FortniteFestivalWeb localStorage keys, their JSON formats, " +
      "valid values, and defaults. Use this to understand what state the app reads on load.",
    inputSchema: {
      type: "object",
      properties: {},
    },
  },
  {
    name: "web_state_reset",
    description:
      "Generate a JavaScript snippet that clears all FortniteFestivalWeb localStorage keys. " +
      "Pass the returned JS to Playwright's page.evaluate() to reset browser state.",
    inputSchema: {
      type: "object",
      properties: {},
    },
  },
  {
    name: "web_state_read",
    description:
      "Generate a JavaScript snippet that reads all FortniteFestivalWeb localStorage keys " +
      "and returns them as a JSON object. Pass the returned JS to Playwright's page.evaluate() " +
      "to inspect current browser state.",
    inputSchema: {
      type: "object",
      properties: {},
    },
  },
];

/* ── Tool handlers ────────────────────────────────────────────────────── */

function resolveInstrument(input) {
  if (!input) return null;
  // Direct instrument key
  if (INSTRUMENTS.includes(input)) return input;
  // Label → key mapping (case-insensitive)
  const lower = input.toLowerCase();
  for (const [key, label] of Object.entries(INSTRUMENT_LABELS)) {
    if (label.toLowerCase() === lower) return key;
  }
  return input; // pass through, app will handle unknown
}

function handleBootstrap(args) {
  const statements = [];

  // Player
  if (args.player === "none") {
    statements.push(`localStorage.removeItem(${JSON.stringify(STORAGE_KEYS.trackedPlayer)});`);
  } else {
    const player = resolvePlayer(args.player);
    statements.push(
      `localStorage.setItem(${JSON.stringify(STORAGE_KEYS.trackedPlayer)}, ${JSON.stringify(JSON.stringify(player))});`
    );
  }

  // First-run experience
  const clearFre = args.clearFre !== false; // default true
  if (clearFre) {
    const freData = buildFirstRunClear(args.clearFre);
    statements.push(
      `localStorage.setItem(${JSON.stringify(STORAGE_KEYS.firstRun)}, ${JSON.stringify(JSON.stringify(freData))});`
    );
  }

  // Song settings
  const instrument = resolveInstrument(args.instrument);
  const songSettings = buildSongSettings({
    sortMode: args.sortMode,
    sortAscending: args.sortAscending,
    instrument,
  });
  statements.push(
    `localStorage.setItem(${JSON.stringify(STORAGE_KEYS.songSettings)}, ${JSON.stringify(JSON.stringify(songSettings))});`
  );

  // Leaderboard settings
  if (args.leaderboardRankBy) {
    const lbSettings = { rankBy: args.leaderboardRankBy };
    statements.push(
      `localStorage.setItem(${JSON.stringify(STORAGE_KEYS.leaderboardSettings)}, ${JSON.stringify(JSON.stringify(lbSettings))});`
    );
  }

  // Dispatch sync events so React picks up changes without reload
  statements.push(`window.dispatchEvent(new Event("fst:trackedPlayerChanged"));`);
  statements.push(`window.dispatchEvent(new Event("fst:songSettingsChanged"));`);

  const js = statements.join("\n");
  return `// FST Web State Bootstrap\n${js}`;
}

function handleDescribe() {
  return `# FortniteFestivalWeb localStorage Keys

## fst:trackedPlayer
JSON: { "accountId": string, "displayName": string }
Registered test accounts:
  - SFentonX: 195e93ef108143b2975ee46662d4d0e1
  - captainparticles: cb8ebb19b32c40d1a736d7f8efec17ac
  - kahnyri: 4c2a1300df4c49a9b9d2b352d704bdf0
Set to null/remove to deselect player.

## fst:firstRun
JSON: Record<slideId, { version: number, hash: string, seenAt: ISO8601 }>
Mark all slides as version 999 to skip first-run carousels.

## fst:songSettings
JSON: {
  sortMode: ${SORT_MODES.join(" | ")},
  sortAscending: boolean,
  metadataOrder: string[] (${DEFAULT_METADATA_ORDER.join(", ")}),
  instrumentOrder: string[] (${INSTRUMENTS.join(", ")}),
  filters: { missingScores, missingFCs, hasScores, hasFCs, overThreshold, seasonFilter, percentileFilter, starsFilter, difficultyFilter },
  instrument: InstrumentKey | null
}
Instrument keys: ${INSTRUMENTS.join(", ")}
Labels: ${Object.entries(INSTRUMENT_LABELS).map(([k, v]) => `${v}=${k}`).join(", ")}

## fst:leaderboardSettings
JSON: { rankBy: ${RANK_BY_MODES.join(" | ")} }
Default: totalscore

## fst-suggestions-filter
JSON: Record<string, boolean> — 104 boolean toggles for suggestion types per instrument.
All default to true. Agents should not typically modify this.

## fst_songs_cache
JSON: { data: { count, currentSeason, songs: [...] }, etag: string|null, v: 2 }
Populated by the API client on first load. Do not manually set — let the app fetch it.`;
}

function handleReset() {
  const keys = Object.values(STORAGE_KEYS);
  const statements = keys.map(
    (k) => `localStorage.removeItem(${JSON.stringify(k)});`
  );
  statements.push(`window.dispatchEvent(new Event("fst:trackedPlayerChanged"));`);
  statements.push(`window.dispatchEvent(new Event("fst:songSettingsChanged"));`);
  return `// FST Web State Reset\n${statements.join("\n")}`;
}

function handleRead() {
  const keyEntries = Object.entries(STORAGE_KEYS)
    .map(([name, key]) => `${JSON.stringify(name)}: (() => { try { const v = localStorage.getItem(${JSON.stringify(key)}); return v ? JSON.parse(v) : null; } catch { return null; } })()`)
    .join(",\n    ");

  return `// FST Web State Read — returns current state as JSON object
(() => ({
    ${keyEntries}
}))()`;
}

const TOOL_HANDLERS = {
  web_state_bootstrap: (args) => handleBootstrap(args || {}),
  web_state_describe: () => handleDescribe(),
  web_state_reset: () => handleReset(),
  web_state_read: () => handleRead(),
};

/* ── Server setup ─────────────────────────────────────────────────────── */

const server = new Server(
  { name: "fst-web-state", version: "1.0.0" },
  { capabilities: { tools: {} } }
);

server.setRequestHandler(ListToolsRequestSchema, async () => ({
  tools: TOOLS,
}));

server.setRequestHandler(CallToolRequestSchema, async (request) => {
  const { name, arguments: args } = request.params;
  const handler = TOOL_HANDLERS[name];

  if (!handler) {
    return {
      content: [{ type: "text", text: `Unknown tool: ${name}` }],
      isError: true,
    };
  }

  try {
    const result = handler(args || {});
    return {
      content: [{ type: "text", text: result }],
    };
  } catch (error) {
    return {
      content: [{ type: "text", text: `Error: ${error.message}` }],
      isError: true,
    };
  }
});

async function main() {
  const transport = new StdioServerTransport();
  await server.connect(transport);
}

main().catch(console.error);
