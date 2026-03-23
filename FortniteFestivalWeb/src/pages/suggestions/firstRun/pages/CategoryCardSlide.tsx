import type { FirstRunSlideDef } from '../../../../firstRun/types';
import CategoryCardDemo from '../demo/CategoryCardDemo';

export const categoryCardSlide: FirstRunSlideDef = {
  id: 'suggestions-category-card',
  version: 1,
  title: 'firstRun.suggestions.categoryCard.title',
  description: 'firstRun.suggestions.categoryCard.description',
  render: () => <CategoryCardDemo />,
  contentStaggerCount: 2,
};
