import type { FirstRunSlideDef } from '../../../../firstRun/types';
import SortDemo from '../demo/SortDemo';

export const sortSlide: FirstRunSlideDef = {
  id: 'songs-sort',
  version: 5,
  title: 'firstRun.songs.sort.title',
  description: 'firstRun.songs.sort.description',
  render: () => <SortDemo />,
  contentStaggerCount: 3,
};
