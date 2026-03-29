import type { FirstRunSlideDef } from '../../../../firstRun/types';
import LeavingTomorrowDemo from '../demo/LeavingTomorrowDemo';

export const leavingTomorrowSlide: FirstRunSlideDef = {
  id: 'songs-leaving-tomorrow',
  version: 1,
  title: 'firstRun.songs.leaving.title',
  description: 'firstRun.songs.leaving.description',
  gate: (ctx) => ctx.shopHighlightEnabled === true,
  render: () => <LeavingTomorrowDemo />,
  contentStaggerCount: 3,
};
