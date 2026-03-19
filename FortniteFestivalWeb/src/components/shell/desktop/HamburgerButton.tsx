import { useTranslation } from 'react-i18next';
import { IoMenu } from 'react-icons/io5';
import { Size } from '@festival/theme';
import css from './HamburgerButton.module.css';

export interface HamburgerButtonProps {
  onClick: () => void;
}

export default function HamburgerButton({ onClick }: HamburgerButtonProps) {
  const { t } = useTranslation();
  return (
    <button
      className={css.button}
      onClick={onClick}
      aria-label={t('aria.openNavigation')}
    >
      <IoMenu size={Size.iconMd} />
    </button>
  );
}
