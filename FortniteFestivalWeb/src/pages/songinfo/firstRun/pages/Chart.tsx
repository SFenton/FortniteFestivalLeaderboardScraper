import type { FirstRunSlideDef } from '../../../../firstRun/types';
import ChartDemo from '../demo/ChartDemo';

export const chartSlide: FirstRunSlideDef = {
  id: 'songinfo-chart',
  version: 2,
  title: 'firstRun.songInfo.chart.title',
  description: 'firstRun.songInfo.chart.description',
  gate: (ctx) => ctx.hasPlayer,
  render: () => <ChartDemo />,
  contentStaggerCount: 3,
};
