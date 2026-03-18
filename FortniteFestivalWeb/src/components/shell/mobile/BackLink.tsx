import { Size } from '@festival/theme';
import { Link, useLocation, useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { IoChevronBack } from 'react-icons/io5';
import css from './BackLink.module.css';

export default function BackLink({ fallback, animate = true }: { fallback: string; animate?: boolean }) {
  const location = useLocation();
  const navigate = useNavigate();
  const { t } = useTranslation();
  const backTo = (location.state as { backTo?: string } | null)?.backTo ?? fallback;

  // Always use history.back() so the destination sees a POP navigation
  // and can restore from cache. The <Link> href is a fallback.
  const handleClick = (e: React.MouseEvent) => {
    e.preventDefault();
    navigate(-1);
  };

  return (
    <div className={`sa-top ${animate ? css.wrapperAnimated : css.wrapper}`}>
      <Link to={backTo} onClick={handleClick} className={css.backLink}>
        <IoChevronBack size={Size.iconBack} />
        {t('common.back')}
      </Link>
    </div>
  );
}
