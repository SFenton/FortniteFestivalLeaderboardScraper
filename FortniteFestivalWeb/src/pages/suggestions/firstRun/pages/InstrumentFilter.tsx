import type { FirstRunSlideDef } from '../../../../firstRun/types';
import InstrumentFilterDemo from '../demo/InstrumentFilterDemo';

export const instrumentFilterSlide: FirstRunSlideDef = {
  id: 'suggestions-instrument-filter',
  version: 1,
  title: 'firstRun.suggestions.instrumentFilter.title',
  description: 'firstRun.suggestions.instrumentFilter.description',
  render: () => <InstrumentFilterDemo />,
  contentStaggerCount: 3,
};
