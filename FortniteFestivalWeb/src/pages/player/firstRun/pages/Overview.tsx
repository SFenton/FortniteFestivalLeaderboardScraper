import type { FirstRunSlideDef } from '../../../../firstRun/types';
import OverviewDemo from '../demo/OverviewDemo';

export const overviewSlide: FirstRunSlideDef = {
  id: 'statistics-overview',
  version: 2,
  title: 'firstRun.statistics.overview.title',
  description: 'firstRun.statistics.overview.description',
  render: () => <OverviewDemo />,
  contentStaggerCount: 3,
};
