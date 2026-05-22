import type { FirstRunSlideDef } from '../../../../firstRun/types';
import NewInShopDemo from '../demo/NewInShopDemo';

export const newInShopSlide: FirstRunSlideDef = {
  id: 'songs-new-in-shop',
  version: 1,
  title: 'firstRun.songs.new.title',
  description: 'firstRun.songs.new.description',
  gate: (ctx) => ctx.shopHighlightEnabled === true,
  render: () => <NewInShopDemo />,
  contentStaggerCount: 3,
};