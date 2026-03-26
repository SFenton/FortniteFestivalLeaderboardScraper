/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useMemo, useEffect, useLayoutEffect, useState, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import {
  Colors, Font, Weight, Gap, Radius, ZIndex, LineHeight, Layout, Opacity,
  CssProp, CssValue,
  padding, transition, transitions, scale,
  modalOverlay, modalCard, btnPrimary, btnDanger, flexRow,
  TRANSITION_MS, MODAL_SCALE_ENTER, FADE_DURATION,
} from '@festival/theme';

export default function ConfirmAlert({
  title,
  message,
  onNo,
  onYes,
}: {
  title: string;
  message: string;
  onNo: () => void;
  onYes: () => void;
}) {
  const { t } = useTranslation();
  const [animIn, setAnimIn] = useState(false);
  const s = useStyles(animIn);

  /* v8 ignore start — animation setup */
  useLayoutEffect(() => {
    const id = requestAnimationFrame(() => setAnimIn(true));
    return () => cancelAnimationFrame(id);
  }, []);
  /* v8 ignore stop */

  useEffect(() => {
    const handleKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onNo(); };
    document.addEventListener('keydown', handleKey);
    return () => document.removeEventListener('keydown', handleKey);
  }, [onNo]);

  return (
    /* v8 ignore start — animation ternaries */
    <div style={s.overlay} onClick={onNo}>
      <div style={s.card} onClick={e => e.stopPropagation()}>
        <div style={s.title}>{title}</div>
        <div style={s.message}>{message}</div>
        <div style={s.buttons}>
          <button style={s.btnNo} onClick={onNo}>{t('common.no')}</button>
          <button style={s.btnYes} onClick={onYes}>{t('common.yes')}</button>
        </div>
      </div>
    </div>
    /* v8 ignore stop */
  );
}

function useStyles(animIn: boolean) {
  return useMemo(() => ({
    overlay: {
      ...modalOverlay,
      zIndex: ZIndex.confirmOverlay,
      opacity: animIn ? 1 : Opacity.none,
      transition: transition(CssProp.opacity, TRANSITION_MS),
    } as CSSProperties,
    card: {
      ...modalCard,
      borderRadius: Radius.md,
      padding: padding(Gap.section),
      maxWidth: Layout.confirmMaxWidth,
      width: '90%',
      opacity: animIn ? 1 : Opacity.none,
      transform: animIn ? scale(1) : scale(MODAL_SCALE_ENTER),
      transition: transitions(
        transition(CssProp.opacity, TRANSITION_MS),
        transition(CssProp.transform, TRANSITION_MS),
      ),
    } as CSSProperties,
    title: {
      fontSize: Font.lg,
      fontWeight: Weight.bold,
      marginBottom: Gap.md,
      opacity: Opacity.none,
      animation: animIn ? `fadeInUp ${TRANSITION_MS}ms ease-out 100ms forwards` : CssValue.none,
    } as CSSProperties,
    message: {
      fontSize: Font.md,
      color: Colors.textSecondary,
      marginBottom: Gap.section,
      lineHeight: LineHeight.snug,
      opacity: Opacity.none,
      animation: animIn ? `fadeInUp ${TRANSITION_MS}ms ease-out 200ms forwards` : CssValue.none,
    } as CSSProperties,
    buttons: {
      ...flexRow,
      gap: Gap.md,
      opacity: Opacity.none,
      animation: animIn ? `fadeInUp ${TRANSITION_MS}ms ease-out ${TRANSITION_MS}ms forwards` : CssValue.none,
    } as CSSProperties,
    btnNo: {
      ...btnPrimary,
      flex: 1,
      padding: padding(Gap.xl),
      fontSize: Font.md,
    } as CSSProperties,
    btnYes: {
      ...btnDanger,
      flex: 1,
      padding: padding(Gap.xl),
      fontSize: Font.md,
    } as CSSProperties,
  }), [animIn]);
}
