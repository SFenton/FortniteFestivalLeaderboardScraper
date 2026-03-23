import type { FirstRunSlideDef } from '../../../../firstRun/types';
import InfiniteScrollDemo from '../demo/InfiniteScrollDemo';

export const infiniteScrollSlide: FirstRunSlideDef = {
  id: 'suggestions-infinite-scroll',
  version: 1,
  title: 'firstRun.suggestions.infiniteScroll.title',
  description: 'firstRun.suggestions.infiniteScroll.description',
  render: () => <InfiniteScrollDemo />,
  contentStaggerCount: 1,
};
