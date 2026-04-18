/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useMemo, useEffect, useLayoutEffect, useState, useCallback, type CSSProperties } from 'react';
import { createPortal } from 'react-dom';
import { useTranslation } from 'react-i18next';
import {
  Colors, Font, Weight, Gap, Radius, ZIndex, LineHeight, Layout, Opacity,
  CssProp, CssValue,
  padding, transition, transitions, scale,
  modalOverlay, modalCard, btnPrimary, btnDanger, flexRow,
  TRANSITION_MS, MODAL_SCALE_ENTER,
} from '@festival/theme';

export default function ConfirmAlert({
  title,
  message,
  onNo,
  onYes,
  onExitComplete,
  noLabel,
  yesLabel,
}: {
  title: string;
  message: string;
  onNo: () => void;
  onYes: () => void;
  /** When provided, enables exit animation: dismiss fades out then calls onExitComplete to unmount. */
  onExitComplete?: () => void;
  /** Custom label for the "No" / dismiss button. Defaults to t('common.no'). */
  noLabel?: string;
  /** Custom label for the "Yes" / confirm button. Defaults to t('common.yes'). */
  yesLabel?: string;
}) {
  const { t } = useTranslation();
  const [animIn, setAnimIn] = useState(false);
  const [animOut, setAnimOut] = useState(false);
  const s = useStyles(animIn, animOut);

  /* v8 ignore start — animation setup */
  useLayoutEffect(() => {
    const id = requestAnimationFrame(() => setAnimIn(true));
    return () => cancelAnimationFrame(id);
  }, []);
  /* v8 ignore stop */

  /* v8 ignore start — exit animation handlers */
  const handleNo = useCallback(() => {
    if (animOut) return;
    if (onExitComplete) {
      setAnimOut(true);
      setTimeout(() => onExitComplete(), TRANSITION_MS);
    } else {
      onNo();
    }
  }, [animOut, onNo, onExitComplete]);

  const handleYes = useCallback(() => {
    if (animOut) return;
    onYes();
    if (onExitComplete) {
      setAnimOut(true);
      setTimeout(() => onExitComplete(), TRANSITION_MS);
    }
  }, [animOut, onYes, onExitComplete]);
  /* v8 ignore stop */

  useEffect(() => {
    const handleKey = (e: KeyboardEvent) => { if (e.key === 'Escape') handleNo(); };
    document.addEventListener('keydown', handleKey);
    return () => document.removeEventListener('keydown', handleKey);
  }, [handleNo]);

  return createPortal(
    /* v8 ignore start — animation ternaries */
    <div style={s.overlay} onClick={e => { e.stopPropagation(); handleNo(); }} data-glow-scope="">
      <div style={s.card} onClick={e => e.stopPropagation()}>
        <div style={s.title}>{title}</div>
        <div style={s.message}>{message}</div>
        <div style={s.buttons}>
          <button style={s.btnNo} onClick={handleNo}>{noLabel ?? t('common.no')}</button>
          <button style={s.btnYes} onClick={handleYes}>{yesLabel ?? t('common.yes')}</button>
        </div>
      </div>
    </div>,
    /* v8 ignore stop */
    document.body,
  );
}

function useStyles(animIn: boolean, animOut: boolean) {
  return useMemo(() => {
    const viewportGutter = Gap.section * 2;
    const maxViewportWidth = `calc(100vw - ${viewportGutter}px)`;

    return ({
    overlay: {
      ...modalOverlay,
      zIndex: ZIndex.confirmOverlay,
      opacity: animOut ? Opacity.none : animIn ? 1 : Opacity.none,
      transition: transition(CssProp.opacity, TRANSITION_MS),
      pointerEvents: animOut ? 'none' as const : undefined,
    } as CSSProperties,
    card: {
      ...modalCard,
      borderRadius: Radius.md,
      padding: padding(Gap.section),
      minWidth: `min(${Layout.confirmMinWidth}px, ${maxViewportWidth})`,
      width: 'fit-content',
      maxWidth: maxViewportWidth,
      opacity: animOut ? Opacity.none : animIn ? 1 : Opacity.none,
      transform: animOut ? scale(MODAL_SCALE_ENTER) : animIn ? scale(1) : scale(MODAL_SCALE_ENTER),
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
      whiteSpace: 'pre-line' as const,
      opacity: Opacity.none,
      animation: animIn ? `fadeInUp ${TRANSITION_MS}ms ease-out 200ms forwards` : CssValue.none,
    } as CSSProperties,
    buttons: {
      ...flexRow,
      gap: Gap.md,
      flexWrap: 'nowrap' as const,
      opacity: Opacity.none,
      animation: animIn ? `fadeInUp ${TRANSITION_MS}ms ease-out ${TRANSITION_MS}ms forwards` : CssValue.none,
    } as CSSProperties,
    btnNo: {
      ...btnPrimary,
      flex: 1,
      minWidth: 'max-content',
      padding: padding(Gap.xl),
      fontSize: Font.md,
      whiteSpace: 'nowrap' as const,
    } as CSSProperties,
    btnYes: {
      ...btnDanger,
      flex: 1,
      minWidth: 'max-content',
      padding: padding(Gap.xl),
      fontSize: Font.md,
      whiteSpace: 'nowrap' as const,
    } as CSSProperties,
  });
  }, [animIn, animOut]);
}
