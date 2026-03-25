import { IoFlash } from 'react-icons/io5';
import type { FirstRunSlideDef } from '../../../../firstRun/types';
import css from './SuggestionsOverview.module.css';

function SuggestionsPreview() {
  const card = (label: string, tagClass: string, iconColor: string, tag: string) => (
    <div className={css.card}>
      <IoFlash size={18} color={iconColor} />
      <div className={css.cardBody}>
        <div className={css.cardLabel}>{label}</div>
      </div>
      <span className={`${css.tag} ${tagClass}`}>
        {tag}
      </span>
    </div>
  );

  return (
    <div className={css.wrapper}>
      {/* v8 ignore start -- CSS module values always defined at runtime */}
      {card('Close to Full Combo', css.tagGold ?? '', 'var(--color-gold)', 'FC Gap')}
      {card('Top 5% Possible', css.tagBlue ?? '', 'var(--color-accent-blue)', 'Climb')}
      {card('Unplayed on Bass', css.tagPurple ?? '', 'var(--color-accent-purple)', 'New')}
      {/* v8 ignore stop */}
    </div>
  );
}

export const suggestionsOverviewSlide: FirstRunSlideDef = {
  id: 'suggestions-overview',
  version: 1,
  title: 'firstRun.suggestions.overview.title',
  description: 'firstRun.suggestions.overview.description',
  render: () => <SuggestionsPreview />,
  contentStaggerCount: 1,
};
