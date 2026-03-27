import type { FirstRunSlideDef } from '../../../../firstRun/types';
import NavigationDemo from '../demo/NavigationDemo';

export const navigationMobileSlide: FirstRunSlideDef = {
  id: 'songs-navigation',
  version: 5,
  title: 'firstRun.songs.navigation.title',
  description: 'firstRun.songs.navigation.descriptionMobile',
  contentKey: 'songs-navigation',
  render: () => <NavigationDemo />,
  contentStaggerCount: 3,
};

export const navigationDesktopSlide: FirstRunSlideDef = {
  id: 'songs-navigation',
  version: 5,
  title: 'firstRun.songs.navigation.title',
  description: 'firstRun.songs.navigation.descriptionDesktop',
  contentKey: 'songs-navigation',
  render: () => <NavigationDemo />,
  contentStaggerCount: 3,
};
