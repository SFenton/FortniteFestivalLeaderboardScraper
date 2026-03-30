/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useMemo, type CSSProperties } from 'react';
import { IoFlash } from 'react-icons/io5';
import type { FirstRunSlideDef } from '../../../../firstRun/types';
import {
  Colors, Font, Weight, Gap, Radius, Layout, CssValue,
  flexRow, padding,
} from '@festival/theme';

function SuggestionsPreview() {
  const s = useStyles();

  const card = (label: string, tagStyle: CSSProperties, iconColor: string, tag: string) => (
    <div style={s.card}>
      <IoFlash size={18} color={iconColor} />
      <div style={s.cardBody}>
        <div style={s.cardLabel}>{label}</div>
      </div>
      <span style={{ ...s.tag, ...tagStyle }}>
        {tag}
      </span>
    </div>
  );

  return (
    <div style={s.wrapper}>
      {card('Close to Full Combo', s.tagGold, Colors.gold, 'FC Gap')}
      {card('Top 5% Possible', s.tagBlue, Colors.accentBlue, 'Climb')}
      {card('Unplayed on Bass', s.tagPurple, Colors.accentPurple, 'New')}
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

function useStyles() {
  return useMemo(() => ({
    wrapper: {
      width: CssValue.full,
      maxWidth: Layout.searchMaxWidth,
    } as CSSProperties,
    card: {
      ...flexRow,
      gap: Gap.md,
      padding: padding(Gap.md, Gap.lg),
      background: Colors.surfaceElevated,
      borderRadius: Radius.xs,
      marginBottom: Gap.sm,
    } as CSSProperties,
    cardBody: {
      flex: 1,
      minWidth: 0,
    } as CSSProperties,
    cardLabel: {
      fontSize: Font.md,
      fontWeight: Weight.semibold,
      color: Colors.textPrimary,
    } as CSSProperties,
    tag: {
      fontSize: Font.xs,
      fontWeight: Weight.bold,
      padding: padding(Gap.xs, Gap.sm),
      borderRadius: Radius.xs,
      color: Colors.textPrimary,
    } as CSSProperties,
    tagGold: {
      background: Colors.gold,
    } as CSSProperties,
    tagBlue: {
      background: Colors.accentBlue,
    } as CSSProperties,
    tagPurple: {
      background: Colors.accentPurple,
    } as CSSProperties,
  }), []);
}
