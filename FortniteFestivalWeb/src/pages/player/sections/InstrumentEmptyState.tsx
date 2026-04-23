/**
 * Empty-state placeholder for an instrument section on the player page.
 * Rendered when the visible instrument has no scores yet — keeps the
 * section present so users see all instruments they have enabled.
 */
import { type CSSProperties } from 'react';
import { type ServerInstrumentKey as InstrumentKey, serverInstrumentLabel } from '@festival/core/api/serverTypes';
import { Colors, Font, Gap, Weight, TextAlign, flexColumn } from '@festival/theme';

export interface InstrumentEmptyStateProps {
  instrument?: InstrumentKey;
  t: (key: string, opts?: Record<string, unknown>) => string;
  /** When true, omits the bottom margin (e.g. when rendered inside a card). */
  noMargin?: boolean;
  /** Override the default title i18n key. */
  titleKey?: string;
  /** Override the default subtitle i18n key. */
  subtitleKey?: string;
  /** Override the resolved title text directly. */
  titleText?: string;
  /** Override the resolved subtitle text directly. */
  subtitleText?: string;
  /** Optional test id override when no instrument key is present. */
  testId?: string;
  /** Optional style overrides for the container. */
  style?: CSSProperties;
}

export default function InstrumentEmptyState({
  instrument,
  t,
  noMargin,
  titleKey,
  subtitleKey,
  titleText,
  subtitleText,
  testId,
  style,
}: InstrumentEmptyStateProps) {
  const containerStyle = {
    ...emptyStateStyles.container,
    ...(noMargin ? { marginBottom: 0 } : null),
    ...style,
  };

  const resolvedTitle = titleText ?? t(titleKey ?? 'player.noScoresYet');
  const resolvedSubtitle = subtitleText
    ?? (instrument
      ? t(subtitleKey ?? 'player.noScoresYetSubtitle', { instrument: serverInstrumentLabel(instrument) })
      : '');

  return (
    <div data-testid={testId ?? (instrument ? `inst-empty-${instrument}` : undefined)} style={containerStyle}>
      <span style={emptyStateStyles.title}>{resolvedTitle}</span>
      <span style={emptyStateStyles.subtitle}>{resolvedSubtitle}</span>
    </div>
  );
}

const emptyStateStyles = {
  container: {
    ...flexColumn,
    alignItems: 'center',
    justifyContent: 'center',
    gap: Gap.md,
    padding: `${Gap.container}px ${Gap.container}px`,
    marginBottom: Gap.section,
    textAlign: TextAlign.center,
  } as CSSProperties,
  title: {
    fontSize: Font.lg,
    fontWeight: Weight.heavy,
    color: Colors.textPrimary,
  } as CSSProperties,
  subtitle: {
    fontSize: Font.sm,
    color: Colors.textSecondary,
  } as CSSProperties,
};
