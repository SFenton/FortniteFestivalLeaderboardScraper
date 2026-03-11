import { Link, useLocation } from 'react-router-dom';
import { IoChevronBack } from 'react-icons/io5';
import { Colors, Font, Gap, Layout, MaxWidth } from '../theme';

export default function BackLink({ fallback }: { fallback: string }) {
  const location = useLocation();
  const backTo = (location.state as { backTo?: string } | null)?.backTo ?? fallback;

  return (
    <div style={styles.wrapper}>
      <Link to={backTo} style={styles.backLink}>
        <IoChevronBack size={22} />
        Back
      </Link>
    </div>
  );
}

const styles: Record<string, React.CSSProperties> = {
  wrapper: {
    maxWidth: MaxWidth.card,
    margin: '0 auto',
    width: '100%',
    position: 'relative',
    zIndex: 50,
    paddingTop: Layout.paddingTop,
  },
  backLink: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: Gap.sm,
    color: Colors.textPrimary,
    textDecoration: 'none',
    fontSize: Font.title,
    fontWeight: 700,
    padding: `${Gap.md}px ${Layout.paddingHorizontal}px ${Gap.md}px ${Layout.paddingHorizontal - 6}px`,
  },
};
