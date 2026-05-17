/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * Purple branded "Select Player Profile" pill with scale/opacity animation.
 * On mobile, renders as a dark opaque pill with IoPersonAdd icon.
 * On desktop, renders as a full pill with text.
 */
import { useMemo, type CSSProperties, type ReactNode } from 'react';
import { IoPersonAdd } from 'react-icons/io5';
import { useTranslation } from 'react-i18next';
import { ActionPill, ACTION_PILL_TRANSITION } from '../common/ActionPill';
import PressableButton from '../common/PressableButton';
import {
  Colors, Gap, IconSize, Layout, Radius, Font, Weight,
  Position, Align, Cursor, Isolation, PointerEvents, CssProp, BoxSizing,
  transition, transitions, scale, flexCenter, padding, truncate,
  opaqueGlass, purpleGlass,
  TRANSITION_MS, PILL_SCALE_HIDDEN,
} from '@festival/theme';
import anim from '../../styles/animations.module.css';

export interface SelectProfilePillProps {
  visible: boolean;
  onClick: () => void;
  isMobile?: boolean;
  label?: string;
  ariaLabel?: string;
  icon?: ReactNode;
  circleIcon?: ReactNode;
}

export function SelectProfilePill({ visible, onClick, isMobile, label, ariaLabel, icon, circleIcon }: SelectProfilePillProps) {
  const { t } = useTranslation();
  const s = useStyles(visible);
  const buttonLabel = label ?? t('common.selectPlayerProfile');
  const buttonAriaLabel = ariaLabel ?? buttonLabel;
  const pillIcon = icon ?? <IoPersonAdd size={IconSize.action} />;

  if (isMobile) {
    return (
      <PressableButton
        style={s.mobilePill}
        className={visible ? anim.profilePillBreathe : undefined}
        onPress={onClick}
        aria-label={buttonAriaLabel}
        title={buttonLabel}
        data-testid="select-profile-pill"
        tabIndex={visible ? 0 : -1}
      >
        {circleIcon ?? pillIcon}
        <span style={s.mobileLabel}>{buttonLabel}</span>
      </PressableButton>
    );
  }

  return (
    <ActionPill
      icon={pillIcon}
      label={buttonLabel}
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
    mobilePill: {
      ...opaqueGlass,
      minWidth: Layout.pillButtonHeight,
      height: Layout.pillButtonHeight,
      borderRadius: Radius.full,
      ...flexCenter,
      justifyContent: Align.start,
      gap: Gap.md,
      padding: padding(0, Gap.xl, 0, Gap.lg),
      boxSizing: BoxSizing.borderBox,
      maxWidth: `calc(100vw - ${Gap.xl * 2}px)`,
      color: Colors.textPrimary,
      fontSize: Font.sm,
      fontWeight: Weight.semibold,
      cursor: Cursor.pointer,
      flexShrink: 0,
      alignSelf: Align.center,
      position: Position.relative,
      isolation: visible ? Isolation.isolate : undefined,
      transition: transitions(
        transition(CssProp.opacity, TRANSITION_MS),
        transition(CssProp.transform, TRANSITION_MS),
      ),
      opacity: visible ? 1 : 0,
      transform: visible ? scale(1) : scale(PILL_SCALE_HIDDEN),
      pointerEvents: visible ? PointerEvents.auto : PointerEvents.none,
    } as CSSProperties,
    mobileLabel: {
      ...truncate,
      minWidth: 0,
      lineHeight: 1,
    } as CSSProperties,
  }), [visible]);
}
