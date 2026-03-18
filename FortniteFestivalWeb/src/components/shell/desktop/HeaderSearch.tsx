import { useRef, useCallback } from 'react';
import { useNavigate } from 'react-router-dom';
import { useAccountSearch } from '../../../hooks/data/useAccountSearch';
import SearchBar, { type SearchBarRef } from '../../common/SearchBar';
import css from './HeaderSearch.module.css';
import type { AccountSearchResult } from '@festival/core/api/serverTypes';

/**
 * Desktop header search bar — searches for players by username and navigates
 * to their player page on selection.
 */
export default function HeaderSearch() {
  const navigate = useNavigate();
  const searchRef = useRef<SearchBarRef>(null);

  const onSelect = useCallback((r: AccountSearchResult) => {
    navigate(`/player/${r.accountId}`);
  }, [navigate]);

  const s = useAccountSearch(onSelect);

  return (
    <div ref={s.containerRef} className={css.container}>
      <SearchBar
        ref={searchRef}
        className={css.inputWrap}
        value={s.query}
        onChange={s.handleChange}
        placeholder="Search player…"
        onKeyDown={s.handleKeyDown}
        onFocus={() => { if (s.results.length > 0 && !s.isOpen) s.handleChange(s.query); }}
      />
      {s.isOpen && (
        <div className={css.dropdown}>
          {s.results.map((r, i) => (
            <button
              key={r.accountId}
              className={i === s.activeIndex ? css.resultActive : css.result}
              onClick={() => s.selectResult(r)}
              onMouseEnter={() => s.setActiveIndex(i)}
            >
              {r.displayName}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
