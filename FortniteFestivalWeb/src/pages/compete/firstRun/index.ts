import { createElement } from 'react';
import type { FirstRunSlideDef } from '../../../firstRun/types';
import CompeteHubDemo from './demo/CompeteHubDemo';
import CompeteLeaderboardsDemo from './demo/CompeteLeaderboardsDemo';
import CompeteRivalsDemo from './demo/CompeteRivalsDemo';

const competeHubSlide: FirstRunSlideDef = {
  id: 'compete-hub',
  version: 2,
  title: 'firstRun.compete.hub.title',
  description: 'firstRun.compete.hub.description',
  render: () => createElement(CompeteHubDemo),
  contentStaggerCount: 1,
};

const competeLeaderboardsSlide: FirstRunSlideDef = {
  id: 'compete-leaderboards',
  version: 2,
  title: 'firstRun.compete.leaderboards.title',
  description: 'firstRun.compete.leaderboards.description',
  render: () => createElement(CompeteLeaderboardsDemo),
  contentStaggerCount: 6,
};

const competeRivalsSlide: FirstRunSlideDef = {
  id: 'compete-rivals',
  version: 2,
  title: 'firstRun.compete.rivals.title',
  description: 'firstRun.compete.rivals.description',
  gate: (ctx) => !!ctx.hasPlayer,
  render: () => createElement(CompeteRivalsDemo),
  contentStaggerCount: 4,
};

export const competeSlides: FirstRunSlideDef[] = [
  competeHubSlide,
  competeLeaderboardsSlide,
  competeRivalsSlide,
];
