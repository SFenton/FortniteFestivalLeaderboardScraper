/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { IoPeople, IoPerson } from 'react-icons/io5';
import { Colors, IconSize, Border, TRANSITION_MS, CssValue, CssProp, flexCenter, transition, transitions, border } from '@festival/theme';

export type HeaderProfileType = 'none' | 'player' | 'band';

export interface HeaderProfileButtonProps {
  hasPlayer: boolean;
  profileType?: HeaderProfileType;
  onClick: () => void;
}

export default function HeaderProfileButton({ hasPlayer, profileType, onClick }: HeaderProfileButtonProps) {
  const { t } = useTranslation();
  const resolvedProfileType = profileType ?? (hasPlayer ? 'player' : 'none');
  const s = useStyles(resolvedProfileType !== 'none');
  const Icon = resolvedProfileType === 'band' ? IoPeople : IoPerson;
  return (
    <button style={s.button} onClick={onClick} aria-label={t('aria.profile')} data-profile-type={resolvedProfileType}>
      <span style={s.circle}>
        <Icon size={IconSize.xs} />
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
