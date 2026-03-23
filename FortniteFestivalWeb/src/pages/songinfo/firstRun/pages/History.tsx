import type { FirstRunSlideDef } from '../../../../firstRun/types';
import HistoryDemo from '../demo/HistoryDemo';

export const historySlide: FirstRunSlideDef = {
  id: 'songinfo-history',
  version: 1,
  title: 'firstRun.songInfo.history.title',
  description: 'firstRun.songInfo.history.description',
  gate: (ctx) => ctx.hasPlayer,
  render: () => <HistoryDemo />,
  contentStaggerCount: 5,
};
