import { useCallback } from 'react';
import { Link } from 'react-router-dom';
import type { TrackedPlayer } from '../hooks/useTrackedPlayer';
import { useAccountSearch } from '../hooks/useAccountSearch';
import { Colors, Font, Gap, Radius } from '../theme';

type Props = {
  player: TrackedPlayer | null;
  onSelect: (player: TrackedPlayer) => void;
  onClear: () => void;
  isSyncing?: boolean;
};

export default function PlayerSearch({ player, onSelect, onClear, isSyncing }: Props) {
  if (player) {
    return (
      <div style={styles.selectedContainer}>
        {isSyncing && <span style={styles.headerSpinner} />}
        <Link
          to="/statistics"
          style={styles.selectedName}
        >
          {player.displayName}
        </Link>
        <button
          style={styles.clearButton}
          onClick={onClear}
          title="Stop tracking"
        >
          ✕
        </button>
      </div>
    );
  }

  return <SearchInput onSelect={onSelect} />;
}

function SearchInput({ onSelect }: { onSelect: (p: TrackedPlayer) => void }) {
  const handleSelect = useCallback(
    (r: { accountId: string; displayName: string }) =>
      onSelect({ accountId: r.accountId, displayName: r.displayName }),
    [onSelect],
  );
  const s = useAccountSearch(handleSelect);

  return (
    <div ref={s.containerRef} style={styles.searchContainer}>
      <input
        style={styles.searchInput}
        placeholder="Search player…"
        value={s.query}
        onChange={(e) => s.handleChange(e.target.value)}
        onKeyDown={s.handleKeyDown}
        onFocus={() => { if (s.results.length > 0) s.close(); /* reopen */ }}
      />
      {s.loading && <span style={styles.spinner} />}
      {s.isOpen && (
        <div style={styles.dropdown}>
          {s.results.map((r, i) => (
            <button
              key={r.accountId}
              style={{
                ...styles.dropdownItem,
                ...(i === s.activeIndex ? styles.dropdownItemActive : {}),
              }}
              onMouseEnter={() => s.setActiveIndex(i)}
              onClick={() => s.selectResult(r)}
            >
              {r.displayName}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

const styles: Record<string, React.CSSProperties> = {
  selectedContainer: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.md,
  },
  headerSpinner: {
    display: 'inline-block',
    width: 14,
    height: 14,
    border: '2px solid rgba(255,255,255,0.15)',
    borderTopColor: Colors.accentPurple,
    borderRadius: '50%',
    animation: 'spin 0.8s linear infinite',
  },
  selectedName: {
    fontSize: Font.md,
    fontWeight: 600,
    color: Colors.textPrimary,
    textDecoration: 'none',
  },
  clearButton: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: 22,
    height: 22,
    borderRadius: '50%',
    border: `1px solid ${Colors.borderPrimary}`,
    backgroundColor: Colors.backgroundCard,
    color: Colors.textTertiary,
    fontSize: 11,
    cursor: 'pointer',
    padding: 0,
    lineHeight: 1,
  },
  searchContainer: {
    position: 'relative' as const,
  },
  searchInput: {
    width: 200,
    padding: `${Gap.sm}px ${Gap.xl}px`,
    borderRadius: Radius.sm,
    border: `1px solid ${Colors.borderPrimary}`,
    backgroundColor: Colors.backgroundCard,
    color: Colors.textPrimary,
    fontSize: Font.sm,
    outline: 'none',
  },
  spinner: {
    position: 'absolute' as const,
    right: 8,
    top: '50%',
    marginTop: -6,
    width: 12,
    height: 12,
    border: '2px solid rgba(255,255,255,0.15)',
    borderTopColor: Colors.textMuted,
    borderRadius: '50%',
    animation: 'spin 0.8s linear infinite',
  },
  dropdown: {
    position: 'absolute' as const,
    top: '100%',
    left: 0,
    right: 0,
    marginTop: 4,
    backgroundColor: Colors.backgroundCard,
    border: `1px solid ${Colors.borderPrimary}`,
    borderRadius: Radius.sm,
    overflow: 'hidden',
    zIndex: 200,
    boxShadow: '0 8px 24px rgba(0,0,0,0.4)',
  },
  dropdownItem: {
    display: 'block',
    width: '100%',
    padding: `${Gap.md}px ${Gap.xl}px`,
    border: 'none',
    backgroundColor: 'transparent',
    color: Colors.textPrimary,
    fontSize: Font.sm,
    textAlign: 'left' as const,
    cursor: 'pointer',
  },
  dropdownItemActive: {
    backgroundColor: Colors.accentPurpleDark,
  },
};
