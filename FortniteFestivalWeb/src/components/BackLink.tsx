import { Link, useLocation, useNavigate } from 'react-router-dom';
import { IoChevronBack } from 'react-icons/io5';
import css from './BackLink.module.css';

export default function BackLink({ fallback, animate = true }: { fallback: string; animate?: boolean }) {
  const location = useLocation();
  const navigate = useNavigate();
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
        <IoChevronBack size={22} />
        Back
      </Link>
    </div>
  );
}
