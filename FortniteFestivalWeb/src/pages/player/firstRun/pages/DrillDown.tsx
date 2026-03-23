import type { FirstRunSlideDef } from '../../../../firstRun/types';
import DrillDownDemo from '../demo/DrillDownDemo';

export const drillDownSlide: FirstRunSlideDef = {
  id: 'statistics-drill-down',
  version: 1,
  title: 'firstRun.statistics.drillDown.title',
  description: 'firstRun.statistics.drillDown.description',
  render: () => <DrillDownDemo />,
  contentStaggerCount: 2,
};
