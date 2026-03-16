import { useRef, useCallback } from 'react';
import { useNavigate } from 'react-router-dom';
import { IoSearch } from 'react-icons/io5';
import { useAccountSearch } from '../../hooks/useAccountSearch';
import css from './HeaderSearch.module.css';
import type { AccountSearchResult } from '../../models';

/**
 * Desktop header search bar — searches for players by username and navigates
 * to their player page on selection.
 */
export default function HeaderSearch() {
  const navigate = useNavigate();
  const inputRef = useRef<HTMLInputElement>(null);

  const onSelect = useCallback((r: AccountSearchResult) => {
    navigate(`/player/${r.accountId}`);
  }, [navigate]);

  const s = useAccountSearch(onSelect);

  return (
    <div ref={s.containerRef} className={css.container}>
      <div className={css.inputWrap} onClick={() => inputRef.current?.focus()}>
        <IoSearch size={16} className={css.searchIcon} />
        <input
          ref={inputRef}
          className={css.input}
          placeholder="Search player…"
          value={s.query}
          onChange={e => s.handleChange(e.target.value)}
          onKeyDown={s.handleKeyDown}
          onFocus={() => { if (s.results.length > 0 && !s.isOpen) s.handleChange(s.query); }}
        />
      </div>
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
