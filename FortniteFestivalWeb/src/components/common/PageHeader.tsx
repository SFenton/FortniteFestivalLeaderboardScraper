import { useMemo, type ReactNode, type CSSProperties } from 'react';
import { Colors, Font, Weight, Gap, MaxWidth, Layout, BoxSizing, CssValue, padding, flexBetween, flexRow } from '@festival/theme';
import BandFilterPill from './BandFilterPill';
import { useBandFilterAction } from '../../contexts/BandFilterActionContext';

export interface PageHeaderProps {
  /** Page title text. Optional — omit for actions-only headers. */
  title?: ReactNode;
  /** Optional subtitle below the title. */
  subtitle?: string;
  /** Keep one subtitle line reserved even when subtitle is not yet available. */
  reserveSubtitleSpace?: boolean;
  /** Optional action elements (pills, buttons) rendered on the right. */
  actions?: ReactNode;
  /** Vertical alignment for the actions slot relative to the title block. */
  actionsAlign?: 'center' | 'start';
  /** Extra inline styles (e.g. for stagger animation). */
  style?: CSSProperties;
  /** Animation end handler. */
  onAnimationEnd?: (e: React.AnimationEvent<HTMLElement>) => void;
  /** Additional class on outer wrapper. */
  className?: string;
}

/**
 * Standardized page header with consistent alignment.
 *
 * Rendered above the scroll container via portal — no sticky positioning needed.
 * Content scrolls independently below this header.
 */
export default function PageHeader({
  title,
  subtitle,
  reserveSubtitleSpace = false,
  actions,
  actionsAlign = 'center',
  style,
  onAnimationEnd,
  className,
}: PageHeaderProps) {
  const s = useStyles();
  const bandFilterAction = useBandFilterAction();
  const resolvedActions = (bandFilterAction.visible || actions) ? (
    <>
      {bandFilterAction.visible && (
        <BandFilterPill
          label={bandFilterAction.label}
          selectedInstruments={bandFilterAction.selectedInstruments}
          onClick={bandFilterAction.onPress}
        />
      )}
      {actions}
    </>
  ) : null;

  return (
    <div className={className} style={{ ...s.header, ...style }} onAnimationEnd={onAnimationEnd}>
      <div style={s.row}>
        {title && (
        <div style={s.titleArea}>
          {typeof title === 'string' ? (
            <h1 style={s.title}>{title}</h1>
          ) : (
            title
          )}
          {(subtitle || reserveSubtitleSpace) && (
            <div style={subtitle ? s.subtitle : s.subtitleReserved} aria-hidden={subtitle ? undefined : true}>
              {subtitle || '\u00a0'}
            </div>
          )}
        </div>
        )}
        {resolvedActions && <div style={actionsAlign === 'start' ? s.actionsTop : s.actions}>{resolvedActions}</div>}
      </div>
    </div>
  );
}

function useStyles() {
  return useMemo(() => ({
    header: {
      maxWidth: MaxWidth.card,
      margin: CssValue.marginCenter,
      width: CssValue.full,
      padding: padding(Gap.md, Layout.paddingHorizontal),
      boxSizing: BoxSizing.borderBox,
    } as CSSProperties,
    row: {
      ...flexBetween,
      gap: Gap.xl,
      minHeight: Layout.entryRowHeight,
    } as CSSProperties,
    titleArea: {
      flex: 1,
      minWidth: Gap.none,
    } as CSSProperties,
    title: {
      fontSize: Font.title,
      fontWeight: Weight.bold,
      color: Colors.textPrimary,
      margin: Gap.none,
    } as CSSProperties,
    subtitle: {
      fontSize: Font.sm,
      color: Colors.textSubtle,
      marginTop: Gap.xs,
    } as CSSProperties,
    subtitleReserved: {
      fontSize: Font.sm,
      color: Colors.textSubtle,
      marginTop: Gap.xs,
      visibility: 'hidden',
    } as CSSProperties,
    actions: {
      ...flexRow,
      gap: Gap.md,
      flexShrink: 0,
      marginLeft: 'auto',
      alignSelf: 'center',
    } as CSSProperties,
    actionsTop: {
      ...flexRow,
      gap: Gap.md,
      flexShrink: 0,
      marginLeft: 'auto',
      alignSelf: 'flex-start',
    } as CSSProperties,
  }), []);
}
