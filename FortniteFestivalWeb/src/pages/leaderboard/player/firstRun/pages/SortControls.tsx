import type { FirstRunSlideDef } from '../../../../../firstRun/types';
import SortControlsDemo from '../demo/SortControlsDemo';

export const sortMobileSlide: FirstRunSlideDef = {
  id: 'playerhistory-sort',
  version: 1,
  title: 'firstRun.playerHistory.sort.title',
  description: 'firstRun.playerHistory.sort.descriptionMobile',
  contentKey: 'playerhistory-sort',
  render: () => <SortControlsDemo />,
  contentStaggerCount: 3,
};

export const sortDesktopSlide: FirstRunSlideDef = {
  id: 'playerhistory-sort',
  version: 1,
  title: 'firstRun.playerHistory.sort.title',
  description: 'firstRun.playerHistory.sort.descriptionDesktop',
  contentKey: 'playerhistory-sort',
  render: () => <SortControlsDemo />,
  contentStaggerCount: 3,
};
