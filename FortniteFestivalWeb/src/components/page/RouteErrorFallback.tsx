/**
 * Error fallback shown when a lazy-loaded route crashes.
 * Provides a "Go to Songs" link and a "Reload" button.
 */
import { useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import {
  Colors, Font, Weight, Gap, Layout, Radius, Border, Cursor, CssValue, Display,
  flexColumn, flexCenter, padding, border,
} from '@festival/theme';

export default function RouteErrorFallback() {
  const { t } = useTranslation();
  const s = useStyles();
  return (
    <div style={s.container}>
      <h2 style={s.title}>{t('common.error')}</h2>
      <p style={s.message}>{t('error.routeLoadFailed')}</p>
      <div style={s.actions}>
        <a href="#/songs" style={s.linkBtn}>{t('error.goToSongs')}</a>
        <button onClick={() => window.location.reload()} style={s.reloadBtn}>
          {t('common.reload')}
        </button>
      </div>
    </div>
  );
}

function useStyles() {
  return useMemo(() => ({
    container: {
      ...flexColumn,
      ...flexCenter,
      height: CssValue.full,
      minHeight: Layout.errorFallbackMinHeight,
      color: Colors.textPrimary,
      textAlign: 'center',
      padding: Gap.section,
      gap: Gap.xl,
    } as CSSProperties,
    title: {
      fontSize: Font.xl,
      margin: Gap.none,
    } as CSSProperties,
    message: {
      fontSize: Font.md,
      color: Colors.textSecondary,
      margin: Gap.none,
    } as CSSProperties,
    actions: {
      display: Display.flex,
      gap: Gap.md,
    } as CSSProperties,
    linkBtn: {
      padding: padding(Gap.lg, Gap.section),
      borderRadius: Radius.xs,
      border: border(Border.thin, Colors.accentBlue),
      backgroundColor: Colors.chipSelectedBg,
      color: Colors.textPrimary,
      fontSize: Font.md,
      fontWeight: Weight.semibold,
      textDecoration: CssValue.none,
      cursor: Cursor.pointer,
    } as CSSProperties,
    reloadBtn: {
      padding: padding(Gap.lg, Gap.section),
      borderRadius: Radius.xs,
      border: CssValue.none,
      backgroundColor: Colors.accentPurple,
      color: Colors.textPrimary,
      fontSize: Font.md,
      fontWeight: Weight.semibold,
      cursor: Cursor.pointer,
    } as CSSProperties,
  }), []);
}
