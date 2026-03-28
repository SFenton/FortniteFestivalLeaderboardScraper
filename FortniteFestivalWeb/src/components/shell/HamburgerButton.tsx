/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { IoMenu } from 'react-icons/io5';
import { Size, Radius, Colors, flexCenter } from '@festival/theme';

export interface HamburgerButtonProps {
  onClick: () => void;
  size?: number;
  style?: CSSProperties;
}

export default function HamburgerButton({ onClick, size = Size.iconMd, style }: HamburgerButtonProps) {
  const { t } = useTranslation();
  const s = useStyles();
  return (
    <button
      style={style ? { ...s.button, ...style } : s.button}
      onClick={onClick}
      aria-label={t('aria.openNavigation')}
    >
      <IoMenu size={size} />
    </button>
  );
}

function useStyles() {
  return useMemo(() => ({
    button: {
      ...flexCenter,
      width: Size.iconMd,
      height: Size.iconMd,
      background: 'none',
      border: 'none',
      cursor: 'pointer',
      borderRadius: Radius.xs,
      color: Colors.textSecondary,
    },
  }), []);
}
