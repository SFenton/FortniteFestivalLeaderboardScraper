import type { FirstRunSlideDef } from '../../../../firstRun/types';
import SongIconsDemo from '../demo/SongIconsDemo';

export const songIconsSlide: FirstRunSlideDef = {
  id: 'songs-icons',
  version: 3,
  title: 'firstRun.songs.songIcons.title',
  description: 'firstRun.songs.songIcons.description',
  gate: (ctx) => ctx.hasPlayer,
  render: () => <SongIconsDemo />,
  contentStaggerCount: 4,
};
