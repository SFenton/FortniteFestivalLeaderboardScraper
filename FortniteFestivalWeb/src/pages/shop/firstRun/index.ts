import type { FirstRunSlideDef } from '../../../firstRun/types';

const shopOverviewSlide: FirstRunSlideDef = {
  id: 'shop-overview',
  version: 1,
  title: 'firstRun.shop.overview.title',
  description: 'firstRun.shop.overview.description',
  render: () => null,
  contentStaggerCount: 1,
};

const shopViewsSlide: FirstRunSlideDef = {
  id: 'shop-views',
  version: 1,
  title: 'firstRun.shop.views.title',
  description: 'firstRun.shop.views.description',
  render: () => null,
  contentStaggerCount: 1,
};

const shopHighlightingSlide: FirstRunSlideDef = {
  id: 'shop-highlighting',
  version: 1,
  title: 'firstRun.shop.highlighting.title',
  description: 'firstRun.shop.highlighting.description',
  gate: (ctx) => !!ctx.shopHighlightEnabled,
  render: () => null,
  contentStaggerCount: 1,
};

export const shopSlides: FirstRunSlideDef[] = [
  shopOverviewSlide,
  shopViewsSlide,
  shopHighlightingSlide,
];
