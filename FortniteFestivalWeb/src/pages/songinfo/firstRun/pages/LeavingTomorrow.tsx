import type { FirstRunSlideDef } from '../../../../firstRun/types';
import LeavingTomorrowButtonDemo from '../demo/LeavingTomorrowButtonDemo';

export const leavingTomorrowMobileSlide: FirstRunSlideDef = {
  id: 'songinfo-leaving-tomorrow',
  version: 1,
  title: 'firstRun.songInfo.leaving.title',
  description: 'firstRun.songInfo.leaving.descriptionMobile',
  contentKey: 'songinfo-leaving-tomorrow',
  render: () => <LeavingTomorrowButtonDemo mobile />,
  contentStaggerCount: 3,
};

export const leavingTomorrowDesktopSlide: FirstRunSlideDef = {
  id: 'songinfo-leaving-tomorrow',
  version: 1,
  title: 'firstRun.songInfo.leaving.title',
  description: 'firstRun.songInfo.leaving.descriptionDesktop',
  contentKey: 'songinfo-leaving-tomorrow',
  render: () => <LeavingTomorrowButtonDemo />,
  contentStaggerCount: 3,
};
