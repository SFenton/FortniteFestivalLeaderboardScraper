import { memo, useMemo, type CSSProperties } from 'react';
import { Link } from 'react-router-dom';
import { IoChevronForward } from 'react-icons/io5';
import { type ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import {
  Colors, Font, Weight, Gap, InstrumentSize, Layout, Radius, WordBreak,
  frostedCard, flexRow, flexColumn,
} from '@festival/theme';
import { InstrumentIcon } from '../../../components/display/InstrumentIcons';

/** Reusable section heading for the player page (title + optional description).
 *  When `instrument` is provided, renders as a compact row with an instrument icon. */
const PlayerSectionHeading = memo(function PlayerSectionHeading({
  title,
  description,
  instrument,
  compact,
  actionLabel,
  actionTo,
  actionTestId,
}: {
  title: string;
  description?: string;
  /** Show an instrument icon to the left. */
  instrument?: InstrumentKey;
  /** Use tighter top margin (Gap.md instead of Gap.section). */
  compact?: boolean;
  /** Optional header action link label. */
  actionLabel?: string;
  /** Optional header action link target. */
  actionTo?: string;
  /** Optional test id for the action link. */
  actionTestId?: string;
}) {
  const s = useStyles(compact);

  if (instrument) {
    return (
      <div style={s.instCardHeader}>
        <InstrumentIcon instrument={instrument} size={InstrumentSize.md} />
        <div style={s.instTitleCol}>
          <span style={s.instCardTitle}>{title}</span>
          {description && <span style={s.instDesc}>{description}</span>}
        </div>
      </div>
    );
  }

  if (actionLabel && actionTo) {
    return (
      <div style={s.sectionWrapper}>
        <div style={s.sectionHeaderRow}>
          <div style={s.sectionTitleCol}>
            <h2 style={s.sectionTitle}>{title}</h2>
            {description && <p style={s.sectionDesc}>{description}</p>}
          </div>
          <Link to={actionTo} data-testid={actionTestId} aria-label={actionLabel} style={s.sectionActionLink}>
            <span>{actionLabel}</span>
            <IoChevronForward aria-hidden="true" size={16} style={s.sectionActionIcon} />
          </Link>
        </div>
      </div>
    );
  }

  return (
    <div style={s.sectionWrapper}>
      <h2 style={s.sectionTitle}>{title}</h2>
      {description && <p style={s.sectionDesc}>{description}</p>}
    </div>
  );
});

export default PlayerSectionHeading;

/** Style used by InstrumentStatsSection for the instrument card header layout. */
export const instCardHeaderStyle: CSSProperties = {
  ...flexRow,
  gap: Gap.md,
  paddingBottom: Gap.sm,
};

function useStyles(compact?: boolean) {
  return useMemo(() => ({
    sectionWrapper: {
      marginTop: Gap.section,
    } as CSSProperties,
    sectionHeaderRow: {
      ...flexRow,
      alignItems: 'flex-start',
      justifyContent: 'space-between',
      gap: Gap.md,
      minWidth: 0,
    } as CSSProperties,
    sectionTitleCol: {
      ...flexColumn,
      minWidth: 0,
      flex: 1,
    } as CSSProperties,
    sectionTitle: {
      fontSize: Font.xl,
      fontWeight: Weight.heavy,
      color: Colors.textPrimary,
      marginBottom: Gap.xs,
      marginTop: Gap.none,
    } as CSSProperties,
    sectionDesc: {
      fontSize: Font.sm,
      color: Colors.textSecondary,
      marginBottom: Gap.xl,
      marginTop: Gap.none,
      wordWrap: WordBreak.breakWord,
    } as CSSProperties,
    sectionActionLink: {
      ...frostedCard,
      ...flexRow,
      alignItems: 'center',
      justifyContent: 'center',
      gap: Gap.xs,
      flexShrink: 0,
      minHeight: Layout.pillButtonHeight,
      padding: `0 ${Gap.md}px`,
      borderRadius: Radius.full,
      color: Colors.textPrimary,
      fontSize: Font.sm,
      fontWeight: Weight.semibold,
      textDecoration: 'none',
    } as CSSProperties,
    sectionActionIcon: {
      flexShrink: 0,
      color: Colors.textSubtle,
    } as CSSProperties,
    instCardHeader: {
      ...instCardHeaderStyle,
      ...(compact ? { marginTop: Gap.md } : {}),
    } as CSSProperties,
    instTitleCol: {
      ...flexColumn,
      justifyContent: 'center',
      height: InstrumentSize.md,
    } as CSSProperties,
    instCardTitle: {
      fontSize: Font.xl,
      fontWeight: Weight.semibold,
    } as CSSProperties,
    instDesc: {
      fontSize: Font.md,
      color: Colors.textSecondary,
      margin: Gap.none,
    } as CSSProperties,
  }), [compact]);
}
