/**
 * Purple branded "Select Player Profile" pill with scale/opacity animation.
 * Inherits base layout from ActionPill.module.css via CSS composes.
 */
import { IoPerson } from 'react-icons/io5';
import { useTranslation } from 'react-i18next';
import { Size, Gap } from '@festival/theme';
import css from './SelectProfilePill.module.css';

export interface SelectProfilePillProps {
  visible: boolean;
  onClick: () => void;
}

export function SelectProfilePill({ visible, onClick }: SelectProfilePillProps) {
  const { t } = useTranslation();

  return (
    <button
      className={css.pill}
      style={{
        opacity: visible ? 1 : 0,
        transform: visible ? 'scale(1)' : 'scale(0.9)',
        pointerEvents: visible ? 'auto' as const : 'none' as const,
      }}
      onClick={onClick}
    >
      <IoPerson size={Size.iconXs} style={{ marginRight: Gap.md }} />
      {t('common.selectPlayerProfile')}
    </button>
  );
}
