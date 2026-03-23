import type { FirstRunSlideDef } from '../../../../firstRun/types';
import TopScoresDemo from '../demo/TopScoresDemo';

export const topScoresSlide: FirstRunSlideDef = {
  id: 'songinfo-top-scores',
  version: 2,
  title: 'firstRun.songInfo.topScores.title',
  description: 'firstRun.songInfo.topScores.description',
  render: () => <TopScoresDemo />,
  contentStaggerCount: 6,
};
