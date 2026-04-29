export const SEARCH_TARGETS = ['songs', 'players', 'bands'] as const;

export type SearchTarget = typeof SEARCH_TARGETS[number];

export function isSearchTarget(value: unknown): value is SearchTarget {
  return typeof value === 'string' && SEARCH_TARGETS.includes(value as SearchTarget);
}
