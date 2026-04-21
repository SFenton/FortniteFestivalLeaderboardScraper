/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * Purple branded "Select Player Profile" pill with scale/opacity animation.
 * On mobile, renders as a pulsing circle with IoPersonAdd icon.
 * On desktop, renders as a full pill with text.
 */
import { useMemo, type CSSProperties } from 'react';
import { IoPersonAdd } from 'react-icons/io5';
import { useTranslation } from 'react-i18next';
import { ActionPill, ACTION_PILL_TRANSITION } from '../common/ActionPill';
import {
  Colors, IconSize, Layout, Radius,
  Position, Align, Cursor, Isolation, PointerEvents, CssProp,
  transition, transitions, scale, flexCenter,
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
  const s = useStyles(visible);

  if (isMobile) {
    return (
      <button
        type="button"
        style={s.circle}
        className={visible ? anim.profileCircleBreathe : undefined}
        onClick={onClick}
        aria-label={t('common.selectPlayerProfile')}
        tabIndex={visible ? 0 : -1}
      >
        <IoPersonAdd size={IconSize.action} />
      </button>
    );
  }

  return (
    <ActionPill
      icon={<IoPersonAdd size={IconSize.action} />}
      label={t('common.selectPlayerProfile')}
      onClick={onClick}
      style={s.pill}
      tabIndex={visible ? 0 : -1}
    />
  );
}

function useStyles(visible: boolean) {
  return useMemo(() => ({
    pill: {
      ...purpleGlass,
      position: Position.relative,
      backgroundImage: 'none',
      color: Colors.textPrimary,
      transition: transitions(
        ACTION_PILL_TRANSITION,
        transition(CssProp.opacity, TRANSITION_MS),
        transition(CssProp.transform, TRANSITION_MS),
      ),
      opacity: visible ? 1 : 0,
      transform: visible ? scale(1) : scale(PILL_SCALE_HIDDEN),
      pointerEvents: visible ? PointerEvents.auto : PointerEvents.none,
    } as CSSProperties,
    circle: {
      width: Layout.pillButtonHeight,
      height: Layout.pillButtonHeight,
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
  }), [visible]);
}
