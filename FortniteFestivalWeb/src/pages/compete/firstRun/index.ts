import type { FirstRunSlideDef } from '../../../firstRun/types';

const competeHubSlide: FirstRunSlideDef = {
  id: 'compete-hub',
  version: 1,
  title: 'firstRun.compete.hub.title',
  description: 'firstRun.compete.hub.description',
  render: () => null,
  contentStaggerCount: 1,
};

const competeLeaderboardsSlide: FirstRunSlideDef = {
  id: 'compete-leaderboards',
  version: 1,
  title: 'firstRun.compete.leaderboards.title',
  description: 'firstRun.compete.leaderboards.description',
  render: () => null,
  contentStaggerCount: 1,
};

const competeRivalsSlide: FirstRunSlideDef = {
  id: 'compete-rivals',
  version: 1,
  title: 'firstRun.compete.rivals.title',
  description: 'firstRun.compete.rivals.description',
  gate: (ctx) => !!ctx.hasPlayer,
  render: () => null,
  contentStaggerCount: 1,
};

export const competeSlides: FirstRunSlideDef[] = [
  competeHubSlide,
  competeLeaderboardsSlide,
  competeRivalsSlide,
];
