/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { IoPerson } from 'react-icons/io5';
import { Colors, IconSize, Border, TRANSITION_MS, CssValue, CssProp, flexCenter, transition, transitions, border } from '@festival/theme';

export interface HeaderProfileButtonProps {
  hasPlayer: boolean;
  onClick: () => void;
}

export default function HeaderProfileButton({ hasPlayer, onClick }: HeaderProfileButtonProps) {
  const { t } = useTranslation();
  const s = useStyles(hasPlayer);
  return (
    <button style={s.button} onClick={onClick} aria-label={t('aria.profile')}>
      <span style={s.circle}>
        <IoPerson size={IconSize.xs} />
      </span>
    </button>
  );
}

function useStyles(hasPlayer: boolean) {
  return useMemo(() => ({
    button: {
      ...flexCenter,
      background: 'none',
      border: 'none',
      cursor: 'pointer',
      padding: 0,
    },
    circle: {
      ...flexCenter,
      width: IconSize.xl,
      height: IconSize.xl,
      borderRadius: CssValue.circle,
      transition: transitions(transition(CssProp.backgroundColor, TRANSITION_MS), transition(CssProp.borderColor, TRANSITION_MS), transition(CssProp.color, TRANSITION_MS)),
      backgroundColor: hasPlayer ? Colors.surfaceSubtle : Colors.profileInactive,
      border: border(Border.thin, hasPlayer ? Colors.borderSubtle : CssValue.transparent),
      color: hasPlayer ? Colors.textSecondary : Colors.profileInactiveText,
    },
  }), [hasPlayer]);
}
