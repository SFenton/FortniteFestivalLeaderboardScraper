import type { FirstRunSlideDef } from '../../../../firstRun/types';
import ViewAllDemo from '../demo/ViewAllDemo';

export const viewAllSlide: FirstRunSlideDef = {
  id: 'songinfo-view-all',
  version: 2,
  title: 'firstRun.songInfo.viewAll.title',
  description: 'firstRun.songInfo.viewAll.description',
  gate: (ctx) => ctx.hasPlayer,
  render: () => <ViewAllDemo />,
  contentStaggerCount: 5,
};
