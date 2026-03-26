/**
 * Reusable player search bar with autocomplete dropdown.
 * Combines SearchBar + useAccountSearch + dropdown results.
 *
 * Used in: DesktopNav header, Sidebar, and anywhere a player name search is needed.
 */
import { useMemo, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { useAccountSearch } from '../../hooks/data/useAccountSearch';
import SearchBar from '../common/SearchBar';
import { type AccountSearchResult } from '@festival/core/api/serverTypes';
import {
  Colors, Font, Gap, Radius, Border, ZIndex, Layout,
  Display, Position, TextAlign, Cursor, Overflow,
  border, padding,
} from '@festival/theme';

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
  /** Inline style for the outer container. */
  style?: React.CSSProperties;
  /** Inline style for the SearchBar wrapper. */
  searchStyle?: React.CSSProperties;
  /** Re-open results when the input is focused and there are cached results. */
  reopenOnFocus?: boolean;
}

export default function PlayerSearchBar({
  onSelect,
  placeholder,
  searchClassName,
  inputClassName,
  className,
  style: containerStyle,
  searchStyle,
  reopenOnFocus = true,
}: PlayerSearchBarProps) {
  const { t } = useTranslation();
  const handleSelect = useCallback(
    (r: AccountSearchResult) => onSelect(r),
    [onSelect],
  );

  const s = useAccountSearch(handleSelect);
  const styles = useStyles();

  return (
    <div ref={s.containerRef} className={className} style={{ ...styles.container, ...containerStyle }}>
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
        style={searchStyle}
      />
      {s.isOpen && (
        <div style={styles.dropdown}>
          {s.results.map((r, i) => (
            <button
              key={r.accountId}
              style={i === s.activeIndex ? styles.resultActive : styles.result}
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

function useStyles() {
  return useMemo(() => {
    const result = {
      display: Display.block,
      width: '100%',
      padding: padding(Gap.md, Gap.xl),
      textAlign: TextAlign.left,
      color: Colors.textPrimary,
      fontSize: Font.sm,
      background: 'none',
      border: 'none',
      cursor: Cursor.pointer,
    } as const;

    return {
      container: {
        position: Position.relative,
      } as const,
      dropdown: {
        position: Position.absolute,
        top: `calc(100% + ${Layout.dropdownGap}px)`,
        left: 0,
        right: 0,
        borderRadius: Radius.sm,
        backgroundColor: Colors.backgroundCard,
        border: border(Border.thin, Colors.borderPrimary),
        zIndex: ZIndex.modalOverlay,
        maxHeight: Layout.dropdownMaxHeight,
        overflowY: Overflow.auto,
      } as const,
      result,
      resultActive: {
        ...result,
        backgroundColor: Colors.surfaceSubtle,
      } as const,
    };
  }, []);
}
