import { createElement } from 'react';
import type { FirstRunSlideDef } from '../../../firstRun/types';
import RankingsOverviewDemo from './demo/RankingsOverviewDemo';
import RankingMetricsDemo from './demo/RankingMetricsDemo';
import YourRankDemo from './demo/YourRankDemo';

const rankingsOverviewSlide: FirstRunSlideDef = {
  id: 'leaderboards-overview',
  version: 2,
  title: 'firstRun.leaderboards.overview.title',
  description: 'firstRun.leaderboards.overview.description',
  render: () => createElement(RankingsOverviewDemo),
  contentStaggerCount: 4,
};

const rankingMetricsSlide: FirstRunSlideDef = {
  id: 'leaderboards-metrics',
  version: 2,
  title: 'firstRun.leaderboards.metrics.title',
  description: 'firstRun.leaderboards.metrics.description',
  render: () => createElement(RankingMetricsDemo),
  contentStaggerCount: 6,
};

const yourRankSlide: FirstRunSlideDef = {
  id: 'leaderboards-your-rank',
  version: 2,
  title: 'firstRun.leaderboards.yourRank.title',
  description: 'firstRun.leaderboards.yourRank.description',
  gate: (ctx) => !!ctx.hasPlayer,
  render: () => createElement(YourRankDemo),
  contentStaggerCount: 7,
};

export const leaderboardsSlides: FirstRunSlideDef[] = [
  rankingsOverviewSlide,
  rankingMetricsSlide,
  yourRankSlide,
];
