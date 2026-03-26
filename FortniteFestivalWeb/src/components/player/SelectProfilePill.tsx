/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * Purple branded "Select Player Profile" pill with scale/opacity animation.
 */
import { useMemo, type CSSProperties } from 'react';
import { IoPerson } from 'react-icons/io5';
import { useTranslation } from 'react-i18next';
import {
  Colors, IconSize, Font, Weight, Gap, Radius,
  Display, Position, Align, Justify, Cursor, PointerEvents, WhiteSpace, CssProp,
  padding, transition, transitions, scale,
  purpleGlass,
  TRANSITION_MS, PILL_SCALE_HIDDEN,
} from '@festival/theme';

export interface SelectProfilePillProps {
  visible: boolean;
  onClick: () => void;
}

export function SelectProfilePill({ visible, onClick }: SelectProfilePillProps) {
  const { t } = useTranslation();
  const s = useStyles(visible);

  return (
    <button style={s.pill} onClick={onClick}>
      <IoPerson size={IconSize.xs} style={s.icon} />
      {t('common.selectPlayerProfile')}
    </button>
  );
}

function useStyles(visible: boolean) {
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
    icon: {
      marginRight: Gap.md,
    } as CSSProperties,
  }), [visible]);
}
