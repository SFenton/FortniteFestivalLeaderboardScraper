import type { FirstRunSlideDef } from '../../../firstRun/types';

const rivalsOverviewSlide: FirstRunSlideDef = {
  id: 'rivals-overview',
  version: 1,
  title: 'firstRun.rivals.overview.title',
  description: 'firstRun.rivals.overview.description',
  render: () => null,
  contentStaggerCount: 1,
};

const rivalsInstrumentsSlide: FirstRunSlideDef = {
  id: 'rivals-instruments',
  version: 1,
  title: 'firstRun.rivals.instruments.title',
  description: 'firstRun.rivals.instruments.description',
  render: () => null,
  contentStaggerCount: 1,
};

const rivalsDetailSlide: FirstRunSlideDef = {
  id: 'rivals-detail',
  version: 1,
  title: 'firstRun.rivals.detail.title',
  description: 'firstRun.rivals.detail.description',
  render: () => null,
  contentStaggerCount: 1,
};

export const rivalsSlides: FirstRunSlideDef[] = [
  rivalsOverviewSlide,
  rivalsInstrumentsSlide,
  rivalsDetailSlide,
];
