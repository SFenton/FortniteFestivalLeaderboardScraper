import { useTranslation } from 'react-i18next';
import { IoPerson } from 'react-icons/io5';
import { Colors, Size } from '@festival/theme';
import appCss from '../../../App.module.css';

export interface HeaderProfileButtonProps {
  hasPlayer: boolean;
  onClick: () => void;
}

export default function HeaderProfileButton({ hasPlayer, onClick }: HeaderProfileButtonProps) {
  const { t } = useTranslation();
  return (
    <button
      className={appCss.headerProfileBtn}
      onClick={onClick}
      aria-label={t('aria.profile')}
    >
      <span className={appCss.headerProfileCircleBase} style={{
        backgroundColor: hasPlayer ? Colors.surfaceSubtle : Colors.profileInactive,
        border: hasPlayer ? `1px solid ${Colors.borderSubtle}` : '1px solid transparent',
        color: hasPlayer ? Colors.textSecondary : Colors.profileInactiveText,
      }}>
        <IoPerson size={Size.iconXs} />
      </span>
    </button>
  );
}
