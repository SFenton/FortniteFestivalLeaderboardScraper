import { createElement } from 'react';
import type { FirstRunSlideDef } from '../../../firstRun/types';
import ShopOverviewDemo from './demo/ShopOverviewDemo';
import ShopViewsDemo from './demo/ShopViewsDemo';
import ShopHighlightingDemo from './demo/ShopHighlightingDemo';

const shopOverviewSlide: FirstRunSlideDef = {
  id: 'shop-overview',
  version: 2,
  title: 'firstRun.shop.overview.title',
  description: 'firstRun.shop.overview.description',
  render: () => createElement(ShopOverviewDemo),
  contentStaggerCount: 6,
};

const shopViewsSlide: FirstRunSlideDef = {
  id: 'shop-views',
  version: 2,
  title: 'firstRun.shop.views.title',
  description: 'firstRun.shop.views.description',
  render: () => createElement(ShopViewsDemo),
  contentStaggerCount: 4,
};

const shopHighlightingSlide: FirstRunSlideDef = {
  id: 'shop-highlighting',
  version: 2,
  title: 'firstRun.shop.highlighting.title',
  description: 'firstRun.shop.highlighting.description',
  gate: (ctx) => !!ctx.shopHighlightEnabled,
  render: () => createElement(ShopHighlightingDemo),
  contentStaggerCount: 5,
};

export const shopSlides: FirstRunSlideDef[] = [
  shopOverviewSlide,
  shopViewsSlide,
  shopHighlightingSlide,
];
