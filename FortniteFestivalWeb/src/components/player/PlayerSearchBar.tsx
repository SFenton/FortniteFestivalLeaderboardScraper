/**
 * Reusable player search bar with autocomplete dropdown.
 * Combines SearchBar + useAccountSearch + dropdown results.
 *
 * Used in: DesktopNav header, Sidebar, and anywhere a player name search is needed.
 */
import { useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { useAccountSearch } from '../../hooks/data/useAccountSearch';
import SearchBar from '../common/SearchBar';
import { type AccountSearchResult } from '@festival/core/api/serverTypes';
import css from './PlayerSearchBar.module.css';

export interface PlayerSearchBarProps {
  /** Called when a player is selected from results. */
  onSelect: (result: AccountSearchResult) => void;
  /** Placeholder override. Defaults to t('common.searchPlayer'). */
  placeholder?: string;
  /** Extra className for the SearchBar wrapper (e.g. frosted pill style). */
  searchClassName?: string;
  /** Extra className for the input element. */
  inputClassName?: string;
  /** Extra className for the outer container (positioning). */
  className?: string;
  /** Re-open results when the input is focused and there are cached results. */
  reopenOnFocus?: boolean;
}

export default function PlayerSearchBar({
  onSelect,
  placeholder,
  searchClassName,
  inputClassName,
  className,
  reopenOnFocus = true,
}: PlayerSearchBarProps) {
  const { t } = useTranslation();
  const handleSelect = useCallback(
    (r: AccountSearchResult) => onSelect(r),
    [onSelect],
  );

  const s = useAccountSearch(handleSelect);

  const containerClass = className ? `${css.container} ${className}` : css.container;

  return (
    <div ref={s.containerRef} className={containerClass}>
      <SearchBar
        value={s.query}
        onChange={s.handleChange}
        placeholder={placeholder ?? t('common.searchPlayer')}
        onKeyDown={s.handleKeyDown}
        /* v8 ignore start */
        onFocus={reopenOnFocus ? () => { if (s.results.length > 0 && !s.isOpen) s.handleChange(s.query); } : undefined}
        /* v8 ignore stop */
        className={searchClassName}
        inputClassName={inputClassName}
      />
      {s.isOpen && (
        <div className={css.dropdown}>
          {s.results.map((r, i) => (
            <button
              key={r.accountId}
              className={i === s.activeIndex ? css.resultActive : css.result}
              onClick={() => s.selectResult(r)}
              /* v8 ignore start */
              onMouseEnter={() => s.setActiveIndex(i)}
              /* v8 ignore stop */
            >
              {r.displayName}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
