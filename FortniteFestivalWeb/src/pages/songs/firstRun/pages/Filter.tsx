import type { FirstRunSlideDef } from '../../../../firstRun/types';
import FilterDemo from '../demo/FilterDemo';

export const filterSlide: FirstRunSlideDef = {
  id: 'songs-filter',
  version: 4,
  title: 'firstRun.songs.filter.title',
  description: 'firstRun.songs.filter.description',
  gate: (ctx) => ctx.hasPlayer,
  render: () => <FilterDemo />,
  contentStaggerCount: 3,
};
