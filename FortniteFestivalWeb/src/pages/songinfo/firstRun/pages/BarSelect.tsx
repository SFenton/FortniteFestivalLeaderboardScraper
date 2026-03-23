import type { FirstRunSlideDef } from '../../../../firstRun/types';
import BarSelectDemo from '../demo/BarSelectDemo';

export const barSelectSlide: FirstRunSlideDef = {
  id: 'songinfo-bar-select',
  version: 2,
  title: 'firstRun.songInfo.barSelect.title',
  description: 'firstRun.songInfo.barSelect.description',
  gate: (ctx) => ctx.hasPlayer,
  render: () => <BarSelectDemo />,
  contentStaggerCount: 3,
};
