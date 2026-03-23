import type { FirstRunSlideDef } from '../../../../../firstRun/types';
import ScoreListDemo from '../demo/ScoreListDemo';

export const scoreListSlide: FirstRunSlideDef = {
  id: 'playerhistory-score-list',
  version: 1,
  title: 'firstRun.playerHistory.scoreList.title',
  description: 'firstRun.playerHistory.scoreList.description',
  render: () => <ScoreListDemo />,
  contentStaggerCount: 5,
};
