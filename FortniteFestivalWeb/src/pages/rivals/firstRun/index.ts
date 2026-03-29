import { createElement } from 'react';
import type { FirstRunSlideDef } from '../../../firstRun/types';
import RivalsOverviewDemo from './demo/RivalsOverviewDemo';
import RivalsInstrumentsDemo from './demo/RivalsInstrumentsDemo';
import RivalsDetailDemo from './demo/RivalsDetailDemo';

const rivalsOverviewSlide: FirstRunSlideDef = {
  id: 'rivals-overview',
  version: 2,
  title: 'firstRun.rivals.overview.title',
  description: 'firstRun.rivals.overview.description',
  render: () => createElement(RivalsOverviewDemo),
  contentStaggerCount: 4,
};

const rivalsInstrumentsSlide: FirstRunSlideDef = {
  id: 'rivals-instruments',
  version: 2,
  title: 'firstRun.rivals.instruments.title',
  description: 'firstRun.rivals.instruments.description',
  render: () => createElement(RivalsInstrumentsDemo),
  contentStaggerCount: 6,
};

const rivalsDetailSlide: FirstRunSlideDef = {
  id: 'rivals-detail',
  version: 2,
  title: 'firstRun.rivals.detail.title',
  description: 'firstRun.rivals.detail.description',
  render: () => createElement(RivalsDetailDemo),
  contentStaggerCount: 4,
};

export const rivalsSlides: FirstRunSlideDef[] = [
  rivalsOverviewSlide,
  rivalsInstrumentsSlide,
  rivalsDetailSlide,
];
