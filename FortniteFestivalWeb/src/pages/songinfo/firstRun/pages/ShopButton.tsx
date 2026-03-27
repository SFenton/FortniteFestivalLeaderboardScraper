import type { FirstRunSlideDef } from '../../../../firstRun/types';
import ShopButtonDemo from '../demo/ShopButtonDemo';

export const shopButtonMobileSlide: FirstRunSlideDef = {
  id: 'songinfo-shop-button',
  version: 1,
  title: 'firstRun.songInfo.shop.title',
  description: 'firstRun.songInfo.shop.descriptionMobile',
  contentKey: 'songinfo-shop-button',
  render: () => <ShopButtonDemo mobile />,
  contentStaggerCount: 3,
};

export const shopButtonDesktopSlide: FirstRunSlideDef = {
  id: 'songinfo-shop-button',
  version: 1,
  title: 'firstRun.songInfo.shop.title',
  description: 'firstRun.songInfo.shop.descriptionDesktop',
  contentKey: 'songinfo-shop-button',
  render: () => <ShopButtonDemo />,
  contentStaggerCount: 3,
};
