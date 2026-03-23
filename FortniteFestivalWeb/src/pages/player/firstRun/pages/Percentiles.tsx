import type { FirstRunSlideDef } from '../../../../firstRun/types';
import PercentileDemo from '../demo/PercentileDemo';

export const percentilesSlide: FirstRunSlideDef = {
  id: 'statistics-percentiles',
  version: 1,
  title: 'firstRun.statistics.percentiles.title',
  description: 'firstRun.statistics.percentiles.description',
  render: () => <PercentileDemo />,
  contentStaggerCount: 2,
};
