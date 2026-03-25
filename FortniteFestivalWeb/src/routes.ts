/** Centralised route path constants. */
export const Routes = {
  songs: '/songs',
  songDetail: (songId: string) => `/songs/${songId}`,
  leaderboard: (songId: string, instrument: string) => `/songs/${songId}/${instrument}`,
  playerHistory: (songId: string, instrument: string) => `/songs/${songId}/${instrument}/history`,
  player: (accountId: string) => `/player/${accountId}`,
  rivals: '/rivals',
  allRivals: (category: string) => `/rivals/all?category=${encodeURIComponent(category)}`,
  rivalDetail: (rivalId: string, rivalName?: string) =>
    `/rivals/${rivalId}${rivalName ? `?name=${encodeURIComponent(rivalName)}` : ''}`,
  rivalry: (rivalId: string, mode: string) =>
    `/rivals/${rivalId}/rivalry?mode=${encodeURIComponent(mode)}`,
  statistics: '/statistics',
  suggestions: '/suggestions',
  shop: '/shop',
  settings: '/settings',
} as const;

/** Regex patterns for route matching. */
export const RoutePatterns = {
  songDetail: /^\/songs\/[^/]+$/,
  leaderboard: /^\/songs\/[^/]+\/[^/]+$/,
  history: /\/history$/,
  player: /^\/player\//,
  rivals: /^\/rivals$/,
  allRivals: /^\/rivals\/all/,
  rivalDetail: /^\/rivals\/[^/]+$/,
  rivalry: /^\/rivals\/[^/]+\/rivalry/,
} as const;
