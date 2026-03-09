import { useState, useRef, useEffect, useCallback } from 'react';
import { Link } from 'react-router-dom';
import { api } from '../api/client';
import type { AccountSearchResult } from '../models';
import type { TrackedPlayer } from '../hooks/useTrackedPlayer';
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
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<AccountSearchResult[]>([]);
  const [isOpen, setIsOpen] = useState(false);
  const [loading, setLoading] = useState(false);
  const [activeIndex, setActiveIndex] = useState(-1);
  const debounceRef = useRef<ReturnType<typeof setTimeout>>(null);
  const containerRef = useRef<HTMLDivElement>(null);

  const search = useCallback(async (q: string) => {
    if (q.length < 2) {
      setResults([]);
      setIsOpen(false);
      return;
    }
    setLoading(true);
    try {
      const res = await api.searchAccounts(q, 10);
      setResults(res.results);
      setIsOpen(res.results.length > 0);
      setActiveIndex(-1);
    } catch {
      setResults([]);
      setIsOpen(false);
    } finally {
      setLoading(false);
    }
  }, []);

  const handleChange = (value: string) => {
    setQuery(value);
    if (debounceRef.current) clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(() => {
      void search(value.trim());
    }, 300);
  };

  const handleSelect = (result: AccountSearchResult) => {
    onSelect({ accountId: result.accountId, displayName: result.displayName });
    setQuery('');
    setResults([]);
    setIsOpen(false);
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (!isOpen || results.length === 0) return;

    if (e.key === 'ArrowDown') {
      e.preventDefault();
      setActiveIndex((prev) => (prev < results.length - 1 ? prev + 1 : 0));
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      setActiveIndex((prev) => (prev > 0 ? prev - 1 : results.length - 1));
    } else if (e.key === 'Enter' && activeIndex >= 0) {
      e.preventDefault();
      const selected = results[activeIndex];
      if (selected) handleSelect(selected);
    } else if (e.key === 'Escape') {
      setIsOpen(false);
    }
  };

  // Close dropdown on outside click
  useEffect(() => {
    const handleClick = (e: MouseEvent) => {
      if (containerRef.current && !containerRef.current.contains(e.target as Node)) {
        setIsOpen(false);
      }
    };
    document.addEventListener('mousedown', handleClick);
    return () => document.removeEventListener('mousedown', handleClick);
  }, []);

  return (
    <div ref={containerRef} style={styles.searchContainer}>
      <input
        style={styles.searchInput}
        placeholder="Search player…"
        value={query}
        onChange={(e) => handleChange(e.target.value)}
        onKeyDown={handleKeyDown}
        onFocus={() => {
          if (results.length > 0) setIsOpen(true);
        }}
      />
      {loading && <span style={styles.spinner} />}
      {isOpen && (
        <div style={styles.dropdown}>
          {results.map((r, i) => (
            <button
              key={r.accountId}
              style={{
                ...styles.dropdownItem,
                ...(i === activeIndex ? styles.dropdownItemActive : {}),
              }}
              onMouseEnter={() => setActiveIndex(i)}
              onClick={() => handleSelect(r)}
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
