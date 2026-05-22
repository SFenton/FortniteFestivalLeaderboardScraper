import type { FirstRunSlideDef } from '../../../../firstRun/types';
import ShopButtonDemo from '../demo/ShopButtonDemo';

export const newInShopMobileSlide: FirstRunSlideDef = {
  id: 'songinfo-new-in-shop',
  version: 1,
  title: 'firstRun.songInfo.new.title',
  description: 'firstRun.songInfo.new.descriptionMobile',
  contentKey: 'songinfo-new-in-shop',
  render: () => <ShopButtonDemo mobile tone="new" />,
  contentStaggerCount: 3,
};

export const newInShopDesktopSlide: FirstRunSlideDef = {
  id: 'songinfo-new-in-shop',
  version: 1,
  title: 'firstRun.songInfo.new.title',
  description: 'firstRun.songInfo.new.descriptionDesktop',
  contentKey: 'songinfo-new-in-shop',
  render: () => <ShopButtonDemo tone="new" />,
  contentStaggerCount: 3,
};