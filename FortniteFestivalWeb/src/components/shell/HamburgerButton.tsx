/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { IoMenu } from 'react-icons/io5';
import { GeneralSize, Size, Radius, Colors, flexCenter } from '@festival/theme';
import { usePressAction } from '../../hooks/ui/usePressAction';

export interface HamburgerButtonProps {
  onClick: () => void;
  size?: number;
  style?: CSSProperties;
}

export default function HamburgerButton({ onClick, size = Size.iconMd, style }: HamburgerButtonProps) {
  const { t } = useTranslation();
  const s = useStyles();
  const pressHandlers = usePressAction<HTMLButtonElement>({ onPress: onClick });
  return (
    <button
      style={style ? { ...s.button, ...style } : s.button}
      {...pressHandlers}
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
      width: GeneralSize.thumb,
      height: GeneralSize.thumb,
      background: 'none',
      border: 'none',
      cursor: 'pointer',
      borderRadius: Radius.xs,
      color: Colors.textSecondary,
      padding: 0,
      lineHeight: 0,
      flexShrink: 0,
    },
  }), []);
}
