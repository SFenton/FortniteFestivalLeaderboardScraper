import { describe, expect, it } from 'vitest';
import type { FirstRunSlideDef } from '../../src/firstRun/types';
import { playerHistorySlides } from '../../src/pages/leaderboard/player/firstRun';
import { competeSlides } from '../../src/pages/compete/firstRun';
import { leaderboardsSlides } from '../../src/pages/leaderboards/firstRun';
import { statisticsSlides } from '../../src/pages/player/firstRun';
import { rivalsSlides } from '../../src/pages/rivals/firstRun';
import { shopSlides } from '../../src/pages/shop/firstRun';
import { songInfoSlides } from '../../src/pages/songinfo/firstRun';
import { songSlides } from '../../src/pages/songs/firstRun';
import { suggestionsSlides } from '../../src/pages/suggestions/firstRun';

type SlideDefinitionMeta = {
  pageKey: string;
  id: string;
  version: number;
  description: string;
  contentKey?: string;
};

type ReplayContractEntry = {
  pageKey: string;
  version: number;
  contentKey?: string;
  replayReason?: string;
};

const LEGACY_SONGS_REPLAY = 'Legacy replay contract from the initial Songs FRE rollout before the replay-policy guardrail.';
const LEGACY_SONGINFO_REPLAY = 'Legacy replay contract from the Song Info FRE polish rollout before the replay-policy guardrail.';
const LEGACY_STATISTICS_REPLAY = 'Legacy replay contract from the initial Statistics FRE rollout before the replay-policy guardrail.';
const LEGACY_LEADERBOARDS_REPLAY = 'Legacy replay contract from the leaderboards rollout before the replay-policy guardrail.';
const LEGACY_COMPETE_REPLAY = 'Legacy replay contract from the compete rollout before the replay-policy guardrail.';
const LEGACY_RIVALS_REPLAY = 'Legacy replay contract from the initial rivals rollout before the replay-policy guardrail.';
const LEGACY_RIVALS_RESPONSIVE_REPLAY = 'Legacy replay contract from responsive/layout revisions before the replay-policy guardrail.';
const LEGACY_SHOP_REPLAY = 'Legacy replay contract from the shop rollout before the replay-policy guardrail.';

const EXPECTED_REGISTERED_FIRST_RUN_REPLAY_CONTRACT = {
  'compete-hub': { pageKey: 'compete', version: 2, replayReason: LEGACY_COMPETE_REPLAY },
  'compete-leaderboards': { pageKey: 'compete', version: 2, replayReason: LEGACY_COMPETE_REPLAY },
  'compete-rivals': { pageKey: 'compete', version: 2, replayReason: LEGACY_COMPETE_REPLAY },
  'leaderboards-experimental-metrics': { pageKey: 'leaderboards', version: 1 },
  'leaderboards-overview': { pageKey: 'leaderboards', version: 2, replayReason: LEGACY_LEADERBOARDS_REPLAY },
  'leaderboards-your-rank': { pageKey: 'leaderboards', version: 2, replayReason: LEGACY_LEADERBOARDS_REPLAY },
  'playerhistory-score-list': { pageKey: 'playerhistory', version: 1 },
  'playerhistory-sort': { pageKey: 'playerhistory', version: 1, contentKey: 'playerhistory-sort' },
  'rivals-detail': { pageKey: 'rivals', version: 3, replayReason: LEGACY_RIVALS_RESPONSIVE_REPLAY },
  'rivals-instruments': { pageKey: 'rivals', version: 3, replayReason: LEGACY_RIVALS_RESPONSIVE_REPLAY },
  'rivals-overview': { pageKey: 'rivals', version: 2, replayReason: LEGACY_RIVALS_REPLAY },
  'shop-highlighting': { pageKey: 'shop', version: 2, replayReason: LEGACY_SHOP_REPLAY },
  'shop-leaving-tomorrow': { pageKey: 'shop', version: 1 },
  'shop-overview': { pageKey: 'shop', version: 2, replayReason: LEGACY_SHOP_REPLAY },
  'shop-views': { pageKey: 'shop', version: 2, replayReason: LEGACY_SHOP_REPLAY },
  'songinfo-bar-select': { pageKey: 'songinfo', version: 2, replayReason: LEGACY_SONGINFO_REPLAY },
  'songinfo-chart': { pageKey: 'songinfo', version: 2, replayReason: LEGACY_SONGINFO_REPLAY },
  'songinfo-leaving-tomorrow': { pageKey: 'songinfo', version: 1, contentKey: 'songinfo-leaving-tomorrow' },
  'songinfo-paths': { pageKey: 'songinfo', version: 2, contentKey: 'songinfo-paths', replayReason: LEGACY_SONGINFO_REPLAY },
  'songinfo-shop-button': { pageKey: 'songinfo', version: 1, contentKey: 'songinfo-shop-button' },
  'songinfo-top-scores': { pageKey: 'songinfo', version: 2, replayReason: LEGACY_SONGINFO_REPLAY },
  'songinfo-view-all': { pageKey: 'songinfo', version: 2, replayReason: LEGACY_SONGINFO_REPLAY },
  'songs-filter': { pageKey: 'songs', version: 4, replayReason: LEGACY_SONGS_REPLAY },
  'songs-icons': { pageKey: 'songs', version: 3, replayReason: LEGACY_SONGS_REPLAY },
  'songs-leaving-tomorrow': { pageKey: 'songs', version: 1 },
  'songs-metadata': { pageKey: 'songs', version: 3, replayReason: LEGACY_SONGS_REPLAY },
  'songs-navigation': { pageKey: 'songs', version: 5, contentKey: 'songs-navigation', replayReason: LEGACY_SONGS_REPLAY },
  'songs-shop-highlight': { pageKey: 'songs', version: 1 },
  'songs-song-list': { pageKey: 'songs', version: 3, replayReason: LEGACY_SONGS_REPLAY },
  'songs-sort': { pageKey: 'songs', version: 5, replayReason: LEGACY_SONGS_REPLAY },
  'statistics-drill-down': { pageKey: 'statistics', version: 1 },
  'statistics-instrument-breakdown': { pageKey: 'statistics', version: 1 },
  'statistics-overview': { pageKey: 'statistics', version: 2, replayReason: LEGACY_STATISTICS_REPLAY },
  'statistics-percentiles': { pageKey: 'statistics', version: 1 },
  'statistics-select-profile': { pageKey: 'statistics', version: 1, contentKey: 'statistics-select-profile' },
  'statistics-top-songs': { pageKey: 'statistics', version: 1 },
  'suggestions-category-card': { pageKey: 'suggestions', version: 1 },
  'suggestions-global-filter': { pageKey: 'suggestions', version: 1 },
  'suggestions-infinite-scroll': { pageKey: 'suggestions', version: 1 },
  'suggestions-instrument-filter': { pageKey: 'suggestions', version: 1 },
} satisfies Record<string, ReplayContractEntry>;

function collectRegisteredSlideDefinitions(): SlideDefinitionMeta[] {
  const sources: Array<{ pageKey: string; slides: FirstRunSlideDef[] }> = [
    { pageKey: 'compete', slides: competeSlides },
    { pageKey: 'leaderboards', slides: leaderboardsSlides },
    { pageKey: 'playerhistory', slides: playerHistorySlides(false) },
    { pageKey: 'playerhistory', slides: playerHistorySlides(true) },
    { pageKey: 'rivals', slides: rivalsSlides },
    { pageKey: 'shop', slides: shopSlides({ viewToggleAvailable: true }) },
    { pageKey: 'songinfo', slides: songInfoSlides(false) },
    { pageKey: 'songinfo', slides: songInfoSlides(true) },
    { pageKey: 'songs', slides: songSlides(false) },
    { pageKey: 'songs', slides: songSlides(true) },
    { pageKey: 'statistics', slides: statisticsSlides(false) },
    { pageKey: 'statistics', slides: statisticsSlides(true) },
    { pageKey: 'suggestions', slides: suggestionsSlides },
  ];

  const seen = new Set<string>();
  const result: SlideDefinitionMeta[] = [];

  for (const { pageKey, slides } of sources) {
    for (const slide of slides) {
      const dedupeKey = [pageKey, slide.id, slide.version, slide.description, slide.contentKey ?? ''].join('|');
      if (seen.has(dedupeKey)) continue;
      seen.add(dedupeKey);
      result.push({
        pageKey,
        id: slide.id,
        version: slide.version,
        description: slide.description,
        ...(slide.contentKey ? { contentKey: slide.contentKey } : {}),
      });
    }
  }

  return result.sort((left, right) => (
    left.pageKey.localeCompare(right.pageKey)
    || left.id.localeCompare(right.id)
    || left.description.localeCompare(right.description)
  ));
}

function collectActualReplayContract() {
  const definitions = collectRegisteredSlideDefinitions();
  const byId = new Map<string, Omit<ReplayContractEntry, 'replayReason'>>();

  for (const definition of definitions) {
    if (!byId.has(definition.id)) {
      byId.set(definition.id, {
        pageKey: definition.pageKey,
        version: definition.version,
        ...(definition.contentKey ? { contentKey: definition.contentKey } : {}),
      });
    }
  }

  return Object.fromEntries(
    [...byId.entries()].sort(([leftId], [rightId]) => leftId.localeCompare(rightId)),
  );
}

describe('registered first-run replay contract', () => {
  it('keeps repeated slide ids on a single replay contract', () => {
    const definitions = collectRegisteredSlideDefinitions();
    const byId = new Map<string, SlideDefinitionMeta[]>();

    for (const definition of definitions) {
      const group = byId.get(definition.id);
      if (group) group.push(definition);
      else byId.set(definition.id, [definition]);
    }

    for (const [id, group] of byId) {
      const pageKeys = new Set(group.map(item => item.pageKey));
      const versions = new Set(group.map(item => item.version));
      expect(pageKeys.size, `${id} should belong to exactly one registered page`).toBe(1);
      expect(versions.size, `${id} should not use different replay versions across definitions`).toBe(1);

      const descriptions = new Set(group.map(item => item.description));
      if (descriptions.size > 1) {
        const sharedContentKeys = new Set(group.map(item => item.contentKey).filter(Boolean));
        expect(sharedContentKeys.size, `${id} should use one shared contentKey across variant descriptions`).toBe(1);
      }
    }
  });

  it('matches the checked-in replay contract metadata', () => {
    const actual = collectActualReplayContract();
    const expected = Object.fromEntries(
      Object.entries(EXPECTED_REGISTERED_FIRST_RUN_REPLAY_CONTRACT)
        .sort(([leftId], [rightId]) => leftId.localeCompare(rightId))
        .map(([id, { replayReason: _replayReason, ...entry }]) => [id, entry]),
    );

    expect(actual).toEqual(expected);
  });

  it('requires an explicit justification for every replaying slide identity', () => {
    const versionedIds = Object.entries(EXPECTED_REGISTERED_FIRST_RUN_REPLAY_CONTRACT)
      .filter(([, entry]) => entry.version > 1)
      .map(([id, entry]) => {
        expect(entry.replayReason?.trim(), `${id} must explain why a dismissed user should see it again`).toBeTruthy();
        return id;
      })
      .sort();

    const actualVersionedIds = Object.entries(collectActualReplayContract())
      .filter(([, entry]) => entry.version > 1)
      .map(([id]) => id)
      .sort();

    expect(versionedIds).toEqual(actualVersionedIds);
  });
});
