/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useRef, useCallback, useMemo, type CSSProperties } from 'react';
import { useNavigate } from 'react-router-dom';
import { useAccountSearch } from '../../../hooks/data/useAccountSearch';
import SearchBar, { type SearchBarRef } from '../../common/SearchBar';
import type { AccountSearchResult } from '@festival/core/api/serverTypes';
import {
  Colors, Font, Gap, Radius, Layout, ZIndex,
  Display, Position, TextAlign, Cursor, Overflow, BoxSizing,
  CssValue, frostedCard, padding,
} from '@festival/theme';

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
  const st = useStyles();

  return (
    <div ref={s.containerRef} style={st.container}>
      <SearchBar
        ref={searchRef}
        style={st.inputWrap}
        value={s.query}
        onChange={s.handleChange}
        placeholder="Search player…"
        onKeyDown={s.handleKeyDown}
        onFocus={() => { if (s.results.length > 0 && !s.isOpen) s.handleChange(s.query); }}
      />
      {s.isOpen && (
        <div style={st.dropdown}>
          {s.results.map((r, i) => (
            <button
              key={r.accountId}
              style={i === s.activeIndex ? st.resultActive : st.result}
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

/** Exported styles for cross-consumer (DesktopNav via PlayerSearchBar). */
export const headerSearchStyles = {
  container: {
    position: Position.relative,
    flex: 1,
    maxWidth: Layout.searchMaxWidth,
  } as CSSProperties,
  inputWrap: {
    ...frostedCard,
    gap: Gap.sm,
    height: Layout.entryRowHeight,
    padding: padding(0, Gap.xl),
    borderRadius: Radius.full,
    boxSizing: BoxSizing.borderBox,
  } as CSSProperties,
};

function useStyles() {
  return useMemo(() => {
    const result: CSSProperties = {
      display: Display.block,
      width: CssValue.full,
      padding: padding(Gap.xl, Gap.section),
      textAlign: TextAlign.left,
      color: Colors.textSecondary,
      fontSize: Font.md,
      background: CssValue.none,
      border: CssValue.none,
      cursor: Cursor.pointer,
    };
    return {
      ...headerSearchStyles,
      dropdown: {
        position: Position.absolute,
        top: `calc(${CssValue.full} + ${Gap.sm}px)`,
        left: 0,
        right: 0,
        ...frostedCard,
        borderRadius: Radius.sm,
        zIndex: ZIndex.searchDropdown,
        maxHeight: Layout.searchDropdownMaxHeight,
        overflowY: Overflow.auto,
      } as CSSProperties,
      result,
      resultActive: {
        ...result,
        backgroundColor: Colors.surfaceSubtle,
      } as CSSProperties,
    };
  }, []);
}
