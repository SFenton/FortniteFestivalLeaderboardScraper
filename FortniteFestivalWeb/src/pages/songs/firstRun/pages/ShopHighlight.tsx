import type { FirstRunSlideDef } from '../../../../firstRun/types';
import ShopHighlightDemo from '../demo/ShopHighlightDemo';

export const shopHighlightSlide: FirstRunSlideDef = {
  id: 'songs-shop-highlight',
  version: 1,
  title: 'firstRun.songs.shop.title',
  description: 'firstRun.songs.shop.description',
  gate: (ctx) => ctx.shopHighlightEnabled === true,
  render: () => <ShopHighlightDemo />,
  contentStaggerCount: 3,
};
