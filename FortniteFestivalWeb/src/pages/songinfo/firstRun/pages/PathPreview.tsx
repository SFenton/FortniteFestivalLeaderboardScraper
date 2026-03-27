import type { FirstRunSlideDef } from '../../../../firstRun/types';
import PathPreviewDemo from '../demo/PathPreviewDemo';

export const pathsMobileSlide: FirstRunSlideDef = {
  id: 'songinfo-paths',
  version: 2,
  title: 'firstRun.songInfo.paths.title',
  description: 'firstRun.songInfo.paths.descriptionMobile',
  contentKey: 'songinfo-paths',
  render: () => <PathPreviewDemo />,
  contentStaggerCount: 3,
};

export const pathsDesktopSlide: FirstRunSlideDef = {
  id: 'songinfo-paths',
  version: 2,
  title: 'firstRun.songInfo.paths.title',
  description: 'firstRun.songInfo.paths.descriptionDesktop',
  contentKey: 'songinfo-paths',
  render: () => <PathPreviewDemo />,
  contentStaggerCount: 3,
};
