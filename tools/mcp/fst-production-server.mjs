#!/usr/bin/env node

/**
 * FST Production MCP Server — Read-only proxy for deployed FSTService API.
 *
 * Environment variables:
 *   FST_BASE_URL  — Production FSTService base URL (required)
 *   FST_API_KEY   — API key for protected endpoints (optional)
 *
 * Exposes read-only tools for querying production data.
 * NO mutation operations — no POST/DELETE/backfill triggers.
 */

import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";

const BASE_URL = process.env.FST_BASE_URL;
const API_KEY = process.env.FST_API_KEY;

if (!BASE_URL) {
  console.error("FST_BASE_URL environment variable is required");
  process.exit(1);
}

async function fstFetch(path, queryParams = {}) {
  const url = new URL(path, BASE_URL);
  for (const [key, value] of Object.entries(queryParams)) {
    if (value !== undefined && value !== null && value !== "") {
      url.searchParams.set(key, String(value));
    }
  }

  const headers = { Accept: "application/json" };
  if (API_KEY) {
    headers["X-API-Key"] = API_KEY;
  }

  const response = await fetch(url.toString(), { headers });
  if (!response.ok) {
    const text = await response.text();
    return { error: true, status: response.status, message: text };
  }
  return response.json();
}

const TOOLS = [
  {
    name: "fst_health",
    description:
      "Check production FSTService health status. Returns service version, uptime, and readiness.",
    inputSchema: {
      type: "object",
      properties: {},
    },
  },
  {
    name: "fst_scrape_progress",
    description:
      "Get current scrape progress. Shows active phase, items processed, rate, and ETA.",
    inputSchema: {
      type: "object",
      properties: {},
    },
  },
  {
    name: "fst_songs",
    description:
      "Get the song catalog. Returns all songs with metadata (title, artist, year, difficulty, instruments).",
    inputSchema: {
      type: "object",
      properties: {},
    },
  },
  {
    name: "fst_player",
    description:
      "Get player profile data including scores across all instruments.",
    inputSchema: {
      type: "object",
      properties: {
        accountId: {
          type: "string",
          description: "Epic Games account ID (32-char hex)",
        },
      },
      required: ["accountId"],
    },
  },
  {
    name: "fst_player_stats",
    description:
      "Get player statistics summary (total scores, percentiles, instrument breakdowns).",
    inputSchema: {
      type: "object",
      properties: {
        accountId: {
          type: "string",
          description: "Epic Games account ID",
        },
      },
      required: ["accountId"],
    },
  },
  {
    name: "fst_player_history",
    description: "Get score history for a player (score changes over time).",
    inputSchema: {
      type: "object",
      properties: {
        accountId: {
          type: "string",
          description: "Epic Games account ID",
        },
      },
      required: ["accountId"],
    },
  },
  {
    name: "fst_leaderboard",
    description: "Get leaderboard for a specific song and instrument.",
    inputSchema: {
      type: "object",
      properties: {
        songId: {
          type: "string",
          description: "Song identifier",
        },
        instrument: {
          type: "string",
          description:
            "Instrument: Solo_Guitar, Solo_Bass, Solo_Drums, Solo_Vocals, Pro_Guitar, Pro_Bass",
        },
        top: {
          type: "number",
          description: "Number of top entries to return (default: 50)",
        },
      },
      required: ["songId", "instrument"],
    },
  },
  {
    name: "fst_account_search",
    description: "Search for an account by display name.",
    inputSchema: {
      type: "object",
      properties: {
        query: {
          type: "string",
          description: "Display name to search for",
        },
      },
      required: ["query"],
    },
  },
  {
    name: "fst_rankings",
    description:
      "Get rankings for a specific instrument (top players by composite score).",
    inputSchema: {
      type: "object",
      properties: {
        instrument: {
          type: "string",
          description:
            "Instrument: Solo_Guitar, Solo_Bass, Solo_Drums, Solo_Vocals, Pro_Guitar, Pro_Bass",
        },
        top: {
          type: "number",
          description: "Number of top entries (default: 50)",
        },
      },
      required: ["instrument"],
    },
  },
  {
    name: "fst_rankings_overview",
    description:
      "Get rankings overview with counts and statistics per instrument.",
    inputSchema: {
      type: "object",
      properties: {},
    },
  },
  {
    name: "fst_rivals",
    description:
      "Get rivals/opps for a player (neighborhood-matched competitors).",
    inputSchema: {
      type: "object",
      properties: {
        accountId: {
          type: "string",
          description: "Epic Games account ID",
        },
      },
      required: ["accountId"],
    },
  },
  {
    name: "fst_shop",
    description: "Get current Fortnite Festival item shop contents.",
    inputSchema: {
      type: "object",
      properties: {},
    },
  },
  {
    name: "fst_features",
    description: "Get current feature flag states from the production service.",
    inputSchema: {
      type: "object",
      properties: {},
    },
  },
  {
    name: "fst_status",
    description:
      "Get service status including last scrape run info and database stats.",
    inputSchema: {
      type: "object",
      properties: {},
    },
  },
];

const TOOL_HANDLERS = {
  fst_health: () => fstFetch("/healthz"),
  fst_scrape_progress: () => fstFetch("/api/progress"),
  fst_songs: () => fstFetch("/api/songs"),
  fst_player: ({ accountId }) => fstFetch(`/api/player/${encodeURIComponent(accountId)}`),
  fst_player_stats: ({ accountId }) =>
    fstFetch(`/api/player/${encodeURIComponent(accountId)}/stats`),
  fst_player_history: ({ accountId }) =>
    fstFetch(`/api/player/${encodeURIComponent(accountId)}/history`),
  fst_leaderboard: ({ songId, instrument, top }) =>
    fstFetch(`/api/leaderboard/${encodeURIComponent(songId)}/${encodeURIComponent(instrument)}`, { top }),
  fst_account_search: ({ query }) =>
    fstFetch("/api/account/search", { q: query }),
  fst_rankings: ({ instrument, top }) =>
    fstFetch(`/api/rankings/${encodeURIComponent(instrument)}`, { top }),
  fst_rankings_overview: () => fstFetch("/api/rankings/overview"),
  fst_rivals: ({ accountId }) =>
    fstFetch(`/api/player/${encodeURIComponent(accountId)}/rivals`),
  fst_shop: () => fstFetch("/api/shop"),
  fst_features: () => fstFetch("/api/features"),
  fst_status: () => fstFetch("/api/status"),
};

const server = new Server(
  { name: "fst-production", version: "1.0.0" },
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
    const result = await handler(args || {});
    return {
      content: [
        {
          type: "text",
          text: JSON.stringify(result, null, 2),
        },
      ],
    };
  } catch (error) {
    return {
      content: [
        {
          type: "text",
          text: `Error calling ${name}: ${error.message}`,
        },
      ],
      isError: true,
    };
  }
});

async function main() {
  const transport = new StdioServerTransport();
  await server.connect(transport);
}

main().catch(console.error);
