import type { RivalSongComparison } from '@festival/core/api/serverTypes';

export type RivalCategorySentiment = 'positive' | 'negative' | 'neutral';

export type RivalCategory = {
  key: string;
  titleKey: string;
  descriptionKey: string;
  songs: RivalSongComparison[];
  sentiment: RivalCategorySentiment;
};

const CLOSEST_BATTLES_COUNT = 5;

/**
 * Splits rival song comparisons into themed categories for the detail page.
 *
 * Positive = user leads (rankDelta > 0 means rival has higher rank number, i.e. worse).
 * Negative = rival leads (rankDelta < 0 means rival has lower rank number, i.e. better).
 *
 * Each directional group is divided into thirds by |rankDelta| magnitude.
 * "Closest Battles" pulls the top N closest songs regardless of direction.
 * A song may appear in both its directional category and "Closest Battles".
 *
 * Empty categories are omitted.
 */
export function categorizeRivalSongs(songs: RivalSongComparison[]): RivalCategory[] {
  if (songs.length === 0) return [];

  const userLeads = songs.filter(s => s.rankDelta > 0).sort((a, b) => a.rankDelta - b.rankDelta);
  const rivalLeads = songs.filter(s => s.rankDelta < 0).sort((a, b) => b.rankDelta - a.rankDelta); // closest first (least negative)
  const ties = songs.filter(s => s.rankDelta === 0);

  // Closest battles: smallest |rankDelta| regardless of direction
  const closestBattles = [...songs]
    .sort((a, b) => Math.abs(a.rankDelta) - Math.abs(b.rankDelta))
    .slice(0, CLOSEST_BATTLES_COUNT);

  const categories: RivalCategory[] = [];

  if (closestBattles.length > 0) {
    categories.push({
      key: 'closest_battles',
      titleKey: 'rivals.detail.closestBattles',
      descriptionKey: 'rivals.detail.closestBattlesDesc',
      songs: closestBattles,
      sentiment: 'neutral',
    });
  }

  // Rival leads: split into "Almost Passed" (close) and "Slipping Away" (far)
  if (rivalLeads.length > 0) {
    const third = Math.ceil(rivalLeads.length / 2);
    const almostPassed = rivalLeads.slice(0, third); // closest (least negative delta)
    const slippingAway = rivalLeads.slice(third);    // farthest (most negative delta)

    if (almostPassed.length > 0) {
      categories.push({
        key: 'almost_passed',
        titleKey: 'rivals.detail.almostPassed',
        descriptionKey: 'rivals.detail.almostPassedDesc',
        songs: almostPassed,
        sentiment: 'negative',
      });
    }
    if (slippingAway.length > 0) {
      categories.push({
        key: 'slipping_away',
        titleKey: 'rivals.detail.slippingAway',
        descriptionKey: 'rivals.detail.slippingAwayDesc',
        songs: slippingAway,
        sentiment: 'negative',
      });
    }
  }

  // User leads: split into "Barely Winning" (close), "Pulling Forward" (mid), "Dominating Them" (far)
  if (userLeads.length > 0) {
    const third = Math.ceil(userLeads.length / 3);
    const barelyWinning = userLeads.slice(0, third);
    const pullingForward = userLeads.slice(third, third * 2);
    const dominatingThem = userLeads.slice(third * 2);

    if (barelyWinning.length > 0) {
      categories.push({
        key: 'barely_winning',
        titleKey: 'rivals.detail.barelyWinning',
        descriptionKey: 'rivals.detail.barelyWinningDesc',
        songs: barelyWinning,
        sentiment: 'positive',
      });
    }
    if (pullingForward.length > 0) {
      categories.push({
        key: 'pulling_forward',
        titleKey: 'rivals.detail.pullingForward',
        descriptionKey: 'rivals.detail.pullingForwardDesc',
        songs: pullingForward,
        sentiment: 'positive',
      });
    }
    if (dominatingThem.length > 0) {
      categories.push({
        key: 'dominating_them',
        titleKey: 'rivals.detail.dominatingThem',
        descriptionKey: 'rivals.detail.dominatingThemDesc',
        songs: dominatingThem,
        sentiment: 'positive',
      });
    }
  }

  // Ties go into closest battles already; if there are extra ties beyond CLOSEST_BATTLES_COUNT
  // they just won't appear in any category, which is fine — exact ties are rare.
  void ties;

  return categories;
}
