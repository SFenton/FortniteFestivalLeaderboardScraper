/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * Brief "Sync complete" banner that auto-dismisses after 3 seconds.
 * Shown after backfill/history/rivals sync finishes so the user always
 * knows their data was updated, even if the progress banner was never visible.
 */
import { memo, useState, useEffect, useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { Colors, Font, Weight, Gap, Radius, frostedCard, flexRow, FADE_DURATION } from '@festival/theme';

const AUTO_DISMISS_MS = 3_000;

interface SyncCompleteBannerProps {
  onDismissed: () => void;
}

const SyncCompleteBanner = memo(function SyncCompleteBanner({ onDismissed }: SyncCompleteBannerProps) {
  const { t } = useTranslation();
  const [exiting, setExiting] = useState(false);
  const s = useStyles(exiting);

  useEffect(() => {
    const timer = setTimeout(() => setExiting(true), AUTO_DISMISS_MS);
    return () => clearTimeout(timer);
  }, []);

  return (
    <div style={s.banner} onAnimationEnd={exiting ? onDismissed : undefined}>
      <span style={s.check}>&#10003;</span>
      <div style={s.text}>
        <span style={s.title}>{t('player.syncComplete')}</span>
        <span style={s.desc}>{t('player.syncCompleteDesc')}</span>
      </div>
    </div>
  );
});

export default SyncCompleteBanner;

function useStyles(exiting: boolean) {
  return useMemo(() => ({
    banner: {
      ...frostedCard,
      ...flexRow,
      gap: Gap.lg,
      padding: Gap.xl,
      borderRadius: Radius.md,
      marginBottom: Gap.md,
      alignItems: 'center',
      animation: exiting
        ? `fadeOutDown ${FADE_DURATION}ms ease-out forwards`
        : `fadeInUp ${FADE_DURATION}ms ease-out forwards`,
    } as CSSProperties,
    check: {
      fontSize: Font.xl,
      color: Colors.statusGreen,
      fontWeight: Weight.bold,
      flexShrink: 0,
    } as CSSProperties,
    text: {
      display: 'flex',
      flexDirection: 'column' as const,
      gap: Gap.xs,
    } as CSSProperties,
    title: {
      fontSize: Font.lg,
      fontWeight: Weight.bold,
      color: Colors.textPrimary,
    } as CSSProperties,
    desc: {
      fontSize: Font.sm,
      color: Colors.textSecondary,
    } as CSSProperties,
  }), [exiting]);
}
