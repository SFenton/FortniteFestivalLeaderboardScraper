import type { ReactNode } from 'react';

export type FirstRunGateContext = {
  hasPlayer: boolean;
  /** True when shop highlighting is active (not hidden + not disabled). */
  shopHighlightEnabled?: boolean;
  /** True when the experimental leaderboard ranks setting is enabled. */
  experimentalRanksEnabled?: boolean;
  /** When false, the carousel waits to show until context stabilizes. Default: true. */
  ready?: boolean;
  /** When true, bypass seen-state and always show all gate-passing slides. */
  alwaysShow?: boolean;
};

export type FirstRunSlideDef = {
  /** Unique slide ID, e.g. "songs-list-overview" */
  id: string;
  /** Bump for major content rewrites */
  version: number;
  /** i18n key for the slide title */
  title: string;
  /** i18n key for the slide description */
  description: string;
  /**
   * Override the string hashed to detect content changes.
   * When set, used instead of `title + description` so that platform-variant
   * slides (same id, different descriptions) share a single seen-record hash.
   */
  contentKey?: string;
  /** Predicate — slide only shown when this returns true. Omit for "always show". */
  gate?: (ctx: FirstRunGateContext) => boolean;
  /** Render the live component preview for this slide */
  render: () => ReactNode;
  /** Number of stagger beats the content uses before title should appear.
   *  Title appears at `count * STAGGER_INTERVAL`, description one beat later. */
  contentStaggerCount?: number;
};

export type FirstRunSeenRecord = {
  version: number;
  hash: string;
  seenAt: string;
};

export type FirstRunStorage = Record<string, FirstRunSeenRecord>;

const STORAGE_KEY = 'fst:firstRun';

export function loadSeenSlides(): FirstRunStorage {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return {};
    return JSON.parse(raw) as FirstRunStorage;
  } catch {
    return {};
  }
}

export function saveSeenSlides(data: FirstRunStorage): void {
  localStorage.setItem(STORAGE_KEY, JSON.stringify(data));
}

/**
 * Simple djb2-style hash of a string, returned as a hex string.
 * Used to detect text changes in slide titles/descriptions.
 */
const DJB2_SEED = 5381;
const DJB2_SHIFT = 5;
const HEX_RADIX = 16;

export function contentHash(text: string): string {
  let hash = DJB2_SEED;
  for (let i = 0; i < text.length; i++) {
    hash = ((hash << DJB2_SHIFT) + hash + text.charCodeAt(i)) | 0;
  }
  return (hash >>> 0).toString(HEX_RADIX);
}

/** Check whether a slide is unseen (missing, newer version, or changed hash). */
export function isSlideUnseen(slide: FirstRunSlideDef, seen: FirstRunStorage): boolean {
  const record = seen[slide.id];
  if (!record) return true;
  if (slide.version > record.version) return true;
  const hash = contentHash(slide.contentKey ?? (slide.title + slide.description));
  if (hash !== record.hash) return true;
  return false;
}
