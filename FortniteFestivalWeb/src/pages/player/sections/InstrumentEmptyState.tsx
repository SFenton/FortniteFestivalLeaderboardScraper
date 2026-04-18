/**
 * Empty-state placeholder for an instrument section on the player page.
 * Rendered when the visible instrument has no scores yet — keeps the
 * section present so users see all instruments they have enabled.
 */
import { type CSSProperties } from 'react';
import { type ServerInstrumentKey as InstrumentKey, serverInstrumentLabel } from '@festival/core/api/serverTypes';
import { Colors, Font, Gap, Weight, TextAlign, flexColumn } from '@festival/theme';

export interface InstrumentEmptyStateProps {
  instrument: InstrumentKey;
  t: (key: string, opts?: Record<string, unknown>) => string;
  /** When true, omits the bottom margin (e.g. when rendered inside a card). */
  noMargin?: boolean;
  /** Override the default title i18n key. */
  titleKey?: string;
  /** Override the default subtitle i18n key. */
  subtitleKey?: string;
}

export default function InstrumentEmptyState({ instrument, t, noMargin, titleKey, subtitleKey }: InstrumentEmptyStateProps) {
  const containerStyle = noMargin
    ? { ...emptyStateStyles.container, marginBottom: 0 }
    : emptyStateStyles.container;

  return (
    <div data-testid={`inst-empty-${instrument}`} style={containerStyle}>
      <span style={emptyStateStyles.title}>{t(titleKey ?? 'player.noScoresYet')}</span>
      <span style={emptyStateStyles.subtitle}>
        {t(subtitleKey ?? 'player.noScoresYetSubtitle', { instrument: serverInstrumentLabel(instrument) })}
      </span>
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
