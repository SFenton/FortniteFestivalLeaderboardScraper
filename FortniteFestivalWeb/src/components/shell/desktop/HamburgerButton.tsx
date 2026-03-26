/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { IoMenu } from 'react-icons/io5';
import { Size, Radius, Colors, flexCenter } from '@festival/theme';

export interface HamburgerButtonProps {
  onClick: () => void;
}

export default function HamburgerButton({ onClick }: HamburgerButtonProps) {
  const { t } = useTranslation();
  const s = useStyles();
  return (
    <button
      style={s.button}
      onClick={onClick}
      aria-label={t('aria.openNavigation')}
    >
      <IoMenu size={Size.iconMd} />
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
