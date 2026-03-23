import type { FirstRunSlideDef } from '../../../../firstRun/types';
import GlobalFilterDemo from '../demo/GlobalFilterDemo';

export const globalFilterSlide: FirstRunSlideDef = {
  id: 'suggestions-global-filter',
  version: 1,
  title: 'firstRun.suggestions.globalFilter.title',
  description: 'firstRun.suggestions.globalFilter.description',
  render: () => <GlobalFilterDemo />,
  contentStaggerCount: 3,
};
