import { Link, useLocation, useNavigate } from 'react-router-dom';
import { IoChevronBack } from 'react-icons/io5';
import { Colors, Font, Gap, Layout, MaxWidth } from '../theme';

export default function BackLink({ fallback }: { fallback: string }) {
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
    <div className="sa-top" style={{ ...styles.wrapper, animation: 'fadeIn 300ms ease-out' }}>
      <Link to={backTo} onClick={handleClick} style={styles.backLink}>
        <IoChevronBack size={22} />
        Back
      </Link>
    </div>
  );
}

const styles: Record<string, React.CSSProperties> = {
  wrapper: {
    display: 'flex',
    alignItems: 'center',
    padding: `${Layout.paddingTop + Gap.md}px ${Layout.paddingHorizontal}px ${Gap.md}px`,
    maxWidth: MaxWidth.card,
    margin: '0 auto',
    width: '100%',
    boxSizing: 'border-box' as const,
    position: 'relative',
    zIndex: 50,
  },
  backLink: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: Gap.sm,
    color: Colors.textPrimary,
    textDecoration: 'none',
    fontSize: Font.title,
    fontWeight: 700,
    marginLeft: -6,
  },
};
