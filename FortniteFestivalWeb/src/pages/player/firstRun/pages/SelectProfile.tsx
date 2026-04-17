import type { FirstRunSlideDef } from '../../../../firstRun/types';
import SelectProfileDemo from '../demo/SelectProfileDemo';

export const selectProfileMobileSlide: FirstRunSlideDef = {
  id: 'statistics-select-profile',
  version: 1,
  title: 'firstRun.statistics.selectProfile.title',
  description: 'firstRun.statistics.selectProfile.descriptionMobile',
  contentKey: 'statistics-select-profile',
  render: () => <SelectProfileDemo />,
  contentStaggerCount: 1,
};

export const selectProfileDesktopSlide: FirstRunSlideDef = {
  id: 'statistics-select-profile',
  version: 1,
  title: 'firstRun.statistics.selectProfile.title',
  description: 'firstRun.statistics.selectProfile.descriptionDesktop',
  contentKey: 'statistics-select-profile',
  render: () => <SelectProfileDemo />,
  contentStaggerCount: 1,
};
