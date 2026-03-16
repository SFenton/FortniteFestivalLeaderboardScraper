import { useState, useEffect, useCallback, useRef, Fragment } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { IoSearch } from 'react-icons/io5';
import { api } from '../../api/client';
import type { AccountSearchResult } from '../../models';
import { useSearchQuery } from '../../contexts/SearchQueryContext';
import { IS_PWA } from '../../utils/platform';
import { Gap } from '@festival/theme';
import css from './FloatingActionButton.module.css';

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
  const { t } = useTranslation();
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

  const searchQuery = useSearchQuery();
  const [query, setQuery] = useState(mode === 'songs' ? searchQuery.query : '');
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
      searchQuery.setQuery(value);
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
        <div className={css.searchBarOuter} style={{ ...(IS_PWA ? { bottom: 80 + Gap.section - Gap.md } : {}) }}>
          <div className={`fab-search-bar ${css.searchBar}`}>
            <div className={css.searchInputWrap} onClick={() => inputRef.current?.focus()}>
              <IoSearch size={16} className={css.searchIcon} />
              <input
                ref={inputRef}
                className={css.searchInput}
                placeholder={placeholder ?? 'Search player\u2026'}
                value={query}
                onChange={e => handleChange(e.target.value)}
                onKeyDown={handleKeyDown}
                enterKeyHint="done"
              />
            </div>
            {mode === 'players' && results.length > 0 && (
              <div className={css.searchResults}>
                {results.map((r, i) => (
                  <button
                    key={r.accountId}
                    className={i === activeIndex ? css.searchResultBtnActive : css.searchResultBtn}
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
      <div className={css.container} style={{ ...(IS_PWA ? { bottom: 80 + Gap.section - Gap.md } : {}) }}>
        <button
          className={css.fab}
          onClick={() => actionsOpen ? closeActions() : openActions()}
          aria-label={t('common.actions')}
        >
          {icon ?? <span className={css.hamburger}><span className={css.hamburgerLine} /><span className={css.hamburgerLine} /><span className={css.hamburgerLine} /></span>}
        </button>
        {popupMounted && (
          <div
            className={css.popup} style={{
              transform: popupVisible ? 'scale(1)' : 'scale(0)',
              opacity: popupVisible ? 1 : 0,
              transition: popupVisible
                ? 'transform 450ms cubic-bezier(0.34, 1.56, 0.64, 1), opacity 300ms ease'
                : 'transform 300ms ease, opacity 300ms ease',
            }}
          >
            {(actionGroups ?? []).map((group, gi) => (
              <Fragment key={gi}>
                {gi > 0 && <div className={css.popupDivider} />}
                {group.map((action) => (
                  <button key={action.label} className={css.popupItem} onClick={() => { closeActions(); action.onPress(); }}>
                    <span className={css.popupItemIcon}>{action.icon}</span>
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

