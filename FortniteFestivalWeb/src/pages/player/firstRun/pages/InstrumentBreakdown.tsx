import type { FirstRunSlideDef } from '../../../../firstRun/types';
import InstrumentBreakdownDemo from '../demo/InstrumentBreakdownDemo';

export const instrumentBreakdownSlide: FirstRunSlideDef = {
  id: 'statistics-instrument-breakdown',
  version: 1,
  title: 'firstRun.statistics.instrumentBreakdown.title',
  description: 'firstRun.statistics.instrumentBreakdown.description',
  render: () => <InstrumentBreakdownDemo />,
  contentStaggerCount: 3,
};
