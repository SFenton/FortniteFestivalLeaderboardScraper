import type { FirstRunSlideDef } from '../../../../firstRun/types';
import MetadataDemo from '../demo/MetadataDemo';

export const metadataSlide: FirstRunSlideDef = {
  id: 'songs-metadata',
  version: 3,
  title: 'firstRun.songs.metadata.title',
  description: 'firstRun.songs.metadata.description',
  gate: (ctx) => ctx.hasPlayer,
  render: () => <MetadataDemo />,
  contentStaggerCount: 4,
};
