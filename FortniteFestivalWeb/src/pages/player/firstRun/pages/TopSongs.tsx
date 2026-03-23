import type { FirstRunSlideDef } from '../../../../firstRun/types';
import TopSongsDemo from '../demo/TopSongsDemo';

export const topSongsSlide: FirstRunSlideDef = {
  id: 'statistics-top-songs',
  version: 1,
  title: 'firstRun.statistics.topSongs.title',
  description: 'firstRun.statistics.topSongs.description',
  render: () => <TopSongsDemo />,
  contentStaggerCount: 3,
};
