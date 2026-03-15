import { useState, useEffect, useCallback, useRef, Fragment } from 'react';
import { useNavigate } from 'react-router-dom';
import { IoSearch } from 'react-icons/io5';
import { api } from '../../api/client';
import type { AccountSearchResult } from '../../models';
import { useFabSearch } from '../../contexts/FabSearchContext';
import { IS_PWA } from '../../utils/platform';
import { Colors, Font, Gap, Radius, Layout, MaxWidth, frostedCard } from '@festival/theme';

export interface ActionItem {
  label: string;
  icon: React.ReactNode;
  onPress: () => void;
}

interface Props {
  mode: 'players' | 'songs';
  defaultOpen?: boolean;
  placeholder?: string;
  icon?: React.ReactNode;
  actionGroups?: ActionItem[][];
  onPress: () => void;
}

export default function FloatingActionButton({
  mode,
  defaultOpen,
  placeholder,
  icon,
  actionGroups,
  onPress: _onPress,
}: Props) {
  const searchVisible = !!defaultOpen;
  const [actionsOpen, setActionsOpen] = useState(false);
  const [popupMounted, setPopupMounted] = useState(false);
  const [popupVisible, setPopupVisible] = useState(false);

  const openActions = useCallback(() => {
    setActionsOpen(true);
    setPopupMounted(true);
    requestAnimationFrame(() => requestAnimationFrame(() => setPopupVisible(true)));
  }, []);

  const closeActions = useCallback(() => {
    setPopupVisible(false);
    setActionsOpen(false);
    setTimeout(() => { setPopupMounted(false); }, 300);
  }, []);

  const fabSearch = useFabSearch();
  const [query, setQuery] = useState(mode === 'songs' ? fabSearch.query : '');
  const [results, setResults] = useState<AccountSearchResult[]>([]);
  const [activeIndex, setActiveIndex] = useState(-1);
  const debounceRef = useRef<ReturnType<typeof setTimeout>>(null);
  const searchContainerRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  const navigate = useNavigate();

  const searchPlayers = useCallback(async (q: string) => {
    if (q.length < 2) { setResults([]); return; }
    try {
      const res = await api.searchAccounts(q, 10);
      setResults(res.results);
      setActiveIndex(-1);
    } catch { setResults([]); }
  }, []);

  const handleChange = (value: string) => {
    setQuery(value);
    if (mode === 'songs') {
      fabSearch.setQuery(value);
    } else {
      if (debounceRef.current) clearTimeout(debounceRef.current);
      debounceRef.current = setTimeout(() => { void searchPlayers(value.trim()); }, 300);
    }
  };

  const handleSelectPlayer = (r: AccountSearchResult) => {
    navigate(`/player/${r.accountId}`);
    setQuery('');
    setResults([]);
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter') {
      if (mode === 'players' && activeIndex >= 0) {
        e.preventDefault();
        const r = results[activeIndex];
        if (r) handleSelectPlayer(r);
        return;
      }
      (e.target as HTMLInputElement).blur();
      return;
    }
    if (mode !== 'players' || results.length === 0) return;
    if (e.key === 'ArrowDown') { e.preventDefault(); setActiveIndex(p => (p < results.length - 1 ? p + 1 : 0)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setActiveIndex(p => (p > 0 ? p - 1 : results.length - 1)); }
    else if (e.key === 'Escape') { setResults([]); }
  };

  useEffect(() => {
    if (mode !== 'players' || results.length === 0) return;
    const handleClick = (e: MouseEvent) => {
      if (searchContainerRef.current && !searchContainerRef.current.contains(e.target as Node)) setResults([]);
    };
    document.addEventListener('mousedown', handleClick);
    return () => document.removeEventListener('mousedown', handleClick);
  }, [mode, results]);

  useEffect(() => {
    if (!actionsOpen) return;
    const handler = (e: MouseEvent) => {
      if (searchContainerRef.current && !searchContainerRef.current.contains(e.target as Node)) {
        e.stopPropagation();
        closeActions();
      }
    };
    document.addEventListener('click', handler, true);
    return () => document.removeEventListener('click', handler, true);
  }, [actionsOpen, closeActions]);

  useEffect(() => {
    if (searchVisible) setTimeout(() => inputRef.current?.focus(), 50);
  }, [searchVisible]);

  return (
    <div ref={searchContainerRef}>
      {searchVisible && (
        <div style={{ ...styles.searchBarOuter, ...(IS_PWA ? { bottom: 80 + Gap.section - Gap.md } : {}) }}>
          <div className="fab-search-bar" style={styles.searchBar}>
            <div style={styles.searchInputWrap} onClick={() => inputRef.current?.focus()}>
              <IoSearch size={16} style={{ color: Colors.textTertiary, flexShrink: 0 }} />
              <input
                ref={inputRef}
                style={styles.searchInput}
                placeholder={placeholder ?? 'Search player\u2026'}
                value={query}
                onChange={e => handleChange(e.target.value)}
                onKeyDown={handleKeyDown}
                enterKeyHint="done"
              />
            </div>
            {mode === 'players' && results.length > 0 && (
              <div style={styles.searchResults}>
                {results.map((r, i) => (
                  <button
                    key={r.accountId}
                    style={{ ...styles.searchResultBtn, ...(i === activeIndex ? { backgroundColor: Colors.surfaceSubtle } : {}) }}
                    onClick={() => handleSelectPlayer(r)}
                  >
                    {r.displayName}
                  </button>
                ))}
              </div>
            )}
          </div>
        </div>
      )}
      <div style={{ ...styles.container, ...(IS_PWA ? { bottom: 80 + Gap.section - Gap.md } : {}) }}>
        <button
          style={styles.fab}
          onClick={() => actionsOpen ? closeActions() : openActions()}
          aria-label="Actions"
        >
          {icon ?? <span style={styles.hamburger}><span style={styles.hamburgerLine} /><span style={styles.hamburgerLine} /><span style={styles.hamburgerLine} /></span>}
        </button>
        {popupMounted && (
          <div
            style={{
              ...styles.popup,
              ...frostedCard,
              transform: popupVisible ? 'scale(1)' : 'scale(0)',
              opacity: popupVisible ? 1 : 0,
              transition: popupVisible
                ? 'transform 450ms cubic-bezier(0.34, 1.56, 0.64, 1), opacity 300ms ease'
                : 'transform 300ms ease, opacity 300ms ease',
            }}
          >
            {(actionGroups ?? []).map((group, gi) => (
              <Fragment key={gi}>
                {gi > 0 && <div style={styles.popupDivider} />}
                {group.map((action) => (
                  <button key={action.label} style={styles.popupItem} onClick={() => { closeActions(); action.onPress(); }}>
                    <span style={styles.popupItemIcon}>{action.icon}</span>
                    {action.label}
                  </button>
                ))}
              </Fragment>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}

const styles: Record<string, React.CSSProperties> = {
  container: {
    position: 'fixed',
    bottom: 80,
    right: Layout.paddingHorizontal,
    display: 'flex',
    flexDirection: 'column' as const,
    alignItems: 'flex-end',
    gap: Gap.md,
    zIndex: 150,
    pointerEvents: 'none',
  },
  fab: {
    width: 56,
    height: 56,
    borderRadius: '50%',
    ...frostedCard,
    backgroundColor: 'rgb(124, 58, 237)',
    border: '1px solid rgba(124, 58, 237, 0.35)',
    color: Colors.textPrimary,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    cursor: 'pointer',
    boxShadow: '0 4px 16px rgba(0,0,0,0.4)',
    flexShrink: 0,
    pointerEvents: 'auto',
  },
  hamburger: { display: 'flex', flexDirection: 'column' as const, justifyContent: 'center', gap: 5 },
  hamburgerLine: { display: 'block', width: 20, height: 2, backgroundColor: Colors.textPrimary, borderRadius: 1 },
  popup: {
    position: 'absolute',
    bottom: 64,
    right: 0,
    zIndex: 1002,
    pointerEvents: 'auto',
    backgroundColor: Colors.backgroundCard,
    borderRadius: Radius.sm,
    padding: `${Gap.sm}px 0`,
    minWidth: 200,
    whiteSpace: 'nowrap' as const,
    boxShadow: '0 4px 20px rgba(0,0,0,0.4)',
    transformOrigin: 'bottom right',
  },
  popupItem: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.xl,
    width: '100%',
    padding: `${Gap.xl}px ${Gap.section}px`,
    background: 'none',
    border: 'none',
    color: Colors.textSecondary,
    fontSize: Font.md,
    cursor: 'pointer',
    textAlign: 'left' as const,
  },
  popupItemIcon: { display: 'flex', alignItems: 'center', justifyContent: 'center', width: 20, flexShrink: 0, color: Colors.textTertiary },
  popupDivider: { height: 1, backgroundColor: Colors.glassBorder, margin: `${Gap.sm}px 0` },
  searchBarOuter: {
    position: 'fixed',
    bottom: 80,
    left: 0,
    right: 0,
    maxWidth: MaxWidth.card,
    margin: '0 auto',
    padding: `0 ${Layout.paddingHorizontal}px`,
    boxSizing: 'border-box' as const,
    zIndex: 150,
    pointerEvents: 'none',
  },
  searchBar: { display: 'flex', flexDirection: 'column' as const, gap: Gap.sm, position: 'relative', pointerEvents: 'auto' },
  searchInputWrap: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.sm,
    width: '100%',
    height: 56,
    padding: `0 ${Gap.section}px`,
    borderRadius: Radius.full,
    ...frostedCard,
    boxSizing: 'border-box' as const,
    boxShadow: '0 4px 16px rgba(0,0,0,0.3)',
    cursor: 'text',
  },
  searchInput: { flex: 1, background: 'none', border: 'none', outline: 'none', color: Colors.textPrimary, fontSize: Font.md },
  searchResults: {
    position: 'absolute',
    bottom: '100%',
    right: 0,
    left: 0,
    marginBottom: Gap.sm,
    ...frostedCard,
    borderRadius: Radius.sm,
    maxHeight: 360,
    overflowY: 'auto' as const,
    boxShadow: '0 -4px 16px rgba(0,0,0,0.3)',
  },
  searchResultBtn: {
    display: 'block',
    width: '100%',
    padding: `${Gap.xl}px ${Gap.section}px`,
    background: 'none',
    border: 'none',
    color: Colors.textSecondary,
    fontSize: Font.md,
    cursor: 'pointer',
    textAlign: 'left' as const,
  },
};
