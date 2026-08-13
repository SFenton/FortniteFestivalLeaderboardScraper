import { contentHash, type FirstRunSlideDef } from '../../src/firstRun/types';
import { competeSlides } from '../../src/pages/compete/firstRun';
import { playerHistorySlides } from '../../src/pages/leaderboard/player/firstRun';
import { leaderboardsSlides } from '../../src/pages/leaderboards/firstRun';
import { statisticsSlides } from '../../src/pages/player/firstRun';
import { rivalsSlides } from '../../src/pages/rivals/firstRun';
import { shopSlides } from '../../src/pages/shop/firstRun';
import { songInfoSlides } from '../../src/pages/songinfo/firstRun';
import { songSlides } from '../../src/pages/songs/firstRun';
import { suggestionsSlides } from '../../src/pages/suggestions/firstRun';

const allSlides: FirstRunSlideDef[] = [
  ...songSlides(false),
  ...songSlides(true),
  ...songInfoSlides(false),
  ...songInfoSlides(true),
  ...playerHistorySlides(false),
  ...playerHistorySlides(true),
  ...statisticsSlides(false),
  ...statisticsSlides(true),
  ...shopSlides({ viewToggleAvailable: false }),
  ...shopSlides({ viewToggleAvailable: true }),
  ...suggestionsSlides,
  ...leaderboardsSlides,
  ...competeSlides,
  ...rivalsSlides,
];

export function seedAllFirstRunSeen(): void {
  const seen = Object.fromEntries(allSlides.map(slide => [slide.id, {
    version: slide.version,
    hash: contentHash(slide.contentKey ?? (slide.title + slide.description)),
    seenAt: '2026-08-13T00:00:00.000Z',
  }]));
  localStorage.setItem('fst:firstRun', JSON.stringify(seen));
}
