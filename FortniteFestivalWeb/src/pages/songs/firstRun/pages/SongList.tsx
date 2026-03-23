import type { FirstRunSlideDef } from '../../../../firstRun/types';
import SongRowDemo from '../demo/SongRowDemo';

export const songListSlide: FirstRunSlideDef = {
  id: 'songs-song-list',
  version: 3,
  title: 'firstRun.songs.songList.title',
  description: 'firstRun.songs.songList.description',
  render: () => <SongRowDemo />,
  contentStaggerCount: 4,
};
