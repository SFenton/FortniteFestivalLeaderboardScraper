import type { FirstRunSlideDef } from '../../../../firstRun/types';
import NavigationDemo from '../demo/NavigationDemo';

export const navigationMobileSlide: FirstRunSlideDef = {
  id: 'songs-navigation',
  version: 5,
  title: 'firstRun.songs.navigation.title',
  description: 'firstRun.songs.navigation.descriptionMobile',
  render: () => <NavigationDemo />,
  contentStaggerCount: 3,
};

export const navigationDesktopSlide: FirstRunSlideDef = {
  id: 'songs-navigation',
  version: 5,
  title: 'firstRun.songs.navigation.title',
  description: 'firstRun.songs.navigation.descriptionDesktop',
  render: () => <NavigationDemo />,
  contentStaggerCount: 3,
};
