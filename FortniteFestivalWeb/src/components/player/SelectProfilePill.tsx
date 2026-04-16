/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * Purple branded "Select Player Profile" pill with scale/opacity animation.
 * On mobile, renders as a pulsing circle with IoPersonAdd icon.
 * On desktop, renders as a full pill with text.
 */
import { useMemo, type CSSProperties } from 'react';
import { IoPersonAdd } from 'react-icons/io5';
import { useTranslation } from 'react-i18next';
import {
  Colors, IconSize, Font, Weight, Gap, Radius, InstrumentSize,
  Display, Position, Align, Justify, Cursor, Isolation, PointerEvents, WhiteSpace, CssProp,
  padding, transition, transitions, scale, flexCenter,
  purpleGlass,
  TRANSITION_MS, PILL_SCALE_HIDDEN,
} from '@festival/theme';
import anim from '../../styles/animations.module.css';

export interface SelectProfilePillProps {
  visible: boolean;
  onClick: () => void;
  isMobile?: boolean;
}

export function SelectProfilePill({ visible, onClick, isMobile }: SelectProfilePillProps) {
  const { t } = useTranslation();
  const s = useStyles(visible, isMobile);

  if (isMobile) {
    return (
      <button
        style={s.circle}
        className={visible ? anim.profileCircleBreathe : undefined}
        onClick={onClick}
        aria-label={t('common.selectPlayerProfile')}
      >
        <IoPersonAdd size={IconSize.sm} />
      </button>
    );
  }

  return (
    <button style={s.pill} onClick={onClick}>
      <IoPersonAdd size={IconSize.xs} />
      {t('common.selectPlayerProfile')}
    </button>
  );
}

function useStyles(visible: boolean, isMobile?: boolean) {
  return useMemo(() => ({
    pill: {
      // pillBase layout
      display: Display.flex,
      alignItems: Align.center,
      justifyContent: Justify.center,
      position: Position.relative,
      width: 'auto',
      gap: Gap.md,
      height: IconSize.xl,
      borderRadius: Radius.full,
      cursor: Cursor.pointer,
      fontWeight: Weight.semibold,
      whiteSpace: WhiteSpace.nowrap,
      // purpleGlass
      ...purpleGlass,
      // pill-specific
      padding: padding(0, IconSize.md, 0, Gap.section),
      color: Colors.textPrimary,
      fontSize: Font.lg,
      transition: transitions(
        transition(CssProp.opacity, TRANSITION_MS),
        transition(CssProp.transform, TRANSITION_MS),
      ),
      opacity: visible ? 1 : 0,
      transform: visible ? scale(1) : scale(PILL_SCALE_HIDDEN),
      pointerEvents: visible ? PointerEvents.auto : PointerEvents.none,
    } as CSSProperties,
    circle: {
      width: InstrumentSize.lg,
      height: InstrumentSize.lg,
      borderRadius: Radius.full,
      ...flexCenter,
      color: Colors.textPrimary,
      cursor: Cursor.pointer,
      flexShrink: 0,
      alignSelf: Align.center,
      border: 'none',
      position: Position.relative,
      backgroundColor: visible ? Colors.transparent : Colors.accentPurple,
      isolation: visible ? Isolation.isolate : undefined,
      transition: transitions(
        transition(CssProp.opacity, TRANSITION_MS),
        transition(CssProp.transform, TRANSITION_MS),
      ),
      opacity: visible ? 1 : 0,
      transform: visible ? scale(1) : scale(PILL_SCALE_HIDDEN),
      pointerEvents: visible ? PointerEvents.auto : PointerEvents.none,
    } as CSSProperties,
  }), [visible, isMobile]);
}
