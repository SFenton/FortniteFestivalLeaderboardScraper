import type { FirstRunSlideDef } from '../../../firstRun/types';

const rankingsOverviewSlide: FirstRunSlideDef = {
  id: 'leaderboards-overview',
  version: 1,
  title: 'firstRun.leaderboards.overview.title',
  description: 'firstRun.leaderboards.overview.description',
  render: () => null,
  contentStaggerCount: 1,
};

const rankingMetricsSlide: FirstRunSlideDef = {
  id: 'leaderboards-metrics',
  version: 1,
  title: 'firstRun.leaderboards.metrics.title',
  description: 'firstRun.leaderboards.metrics.description',
  render: () => null,
  contentStaggerCount: 1,
};

const yourRankSlide: FirstRunSlideDef = {
  id: 'leaderboards-your-rank',
  version: 1,
  title: 'firstRun.leaderboards.yourRank.title',
  description: 'firstRun.leaderboards.yourRank.description',
  gate: (ctx) => !!ctx.hasPlayer,
  render: () => null,
  contentStaggerCount: 1,
};

export const leaderboardsSlides: FirstRunSlideDef[] = [
  rankingsOverviewSlide,
  rankingMetricsSlide,
  yourRankSlide,
];
