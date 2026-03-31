/**
 * Reusable player search bar with autocomplete dropdown.
 * Combines SearchBar + useAccountSearch + dropdown results.
 *
 * Used in: DesktopNav header, Sidebar, and anywhere a player name search is needed.
 */
/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useState, useMemo, useCallback, useEffect, useRef, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { useAccountSearch } from '../../hooks/data/useAccountSearch';
import { useFadeSpinner } from '../../hooks/ui/useFadeSpinner';
import SearchBar from '../common/SearchBar';
import ArcSpinner, { SpinnerSize } from '../common/ArcSpinner';
import { type AccountSearchResult } from '@festival/core/api/serverTypes';
import {
  Colors, Font, Gap, Radius, ZIndex, Layout,
  Display, Position, TextAlign, Cursor, Overflow, Align, Justify, PointerEvents, CssValue,
  frostedCardSurface, padding, TRANSITION_MS,
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
  const [focused, setFocused] = useState(false);
  const handleSelect = useCallback(
    (r: AccountSearchResult) => { setFocused(false); onSelect(r); },
    [onSelect],
  );

  const s = useAccountSearch(handleSelect);
  const spinner = useFadeSpinner(s.loading || s.debouncing);
  const styles = useStyles();

  const showDropdown = focused;
  const typing = s.query.trim().length >= 2;
  const hasResults = !s.loading && !s.debouncing && s.results.length > 0;
  const noResults = !s.loading && !s.debouncing && typing && s.results.length === 0;
  const idle = !s.loading && !s.debouncing && !typing;

  // Delayed idle hint — waits for dropdown grow to finish, cancelled if user starts typing
  const [hintReady, setHintReady] = useState(false);
  const hintTimerRef = useRef<ReturnType<typeof setTimeout>>(null);
  useEffect(() => {
    if (idle && showDropdown) {
      hintTimerRef.current = setTimeout(() => setHintReady(true), DROPDOWN_ANIM_MS);
    } else {
      if (hintTimerRef.current) clearTimeout(hintTimerRef.current);
      setHintReady(false);
    }
    return () => { if (hintTimerRef.current) clearTimeout(hintTimerRef.current); };
  }, [idle, showDropdown]);

  return (
    <>
      {showDropdown && (
        /* backdrop overlay — click dismisses dropdown */
        <div style={styles.backdrop} onClick={() => setFocused(false)} role="presentation" />
      )}
      <div ref={s.containerRef} className={className} style={{ ...styles.container, ...containerStyle }} {...(showDropdown ? { 'data-glow-scope': '' } : undefined)}>
        <SearchBar
          value={s.query}
          onChange={s.handleChange}
          placeholder={placeholder ?? t('common.searchPlayer')}
          onKeyDown={e => {
            if (e.key === 'Escape') { setFocused(false); return; }
            s.handleKeyDown(e);
          }}
          onFocus={() => {
            setFocused(true);
            /* v8 ignore start */
            if (reopenOnFocus && s.results.length > 0 && !s.isOpen) s.handleChange(s.query);
            /* v8 ignore stop */
          }}
          className={searchClassName}
          inputClassName={inputClassName}
          style={searchStyle}
        />
        <div
          style={showDropdown ? styles.dropdownOpen : styles.dropdownClosed}
        >
        {spinner.visible && (
          <div
            style={{ ...styles.spinnerWrap, opacity: spinner.opacity, transition: `opacity ${TRANSITION_MS}ms ease` }}
            onTransitionEnd={spinner.onTransitionEnd}
          >
            <ArcSpinner size={SpinnerSize.MD} />
          </div>
        )}
        {noResults && !spinner.visible && (
          <div style={styles.hint}>{t('common.noMatchingUsername')}</div>
        )}
        {idle && !spinner.visible && hintReady && (
          <div style={styles.hintAnimated}>{t('common.enterUsername')}</div>
        )}
        {hasResults && !spinner.visible && s.results.map((r, i) => (
          <button
            key={`${s.resultSeq}-${r.accountId}`}
            style={{
              ...(i === s.activeIndex ? styles.resultActive : styles.result),
              opacity: 0,
              animation: `fadeInUp 300ms ease-out ${i * 50}ms forwards`,
            }}
            onClick={() => s.selectResult(r)}
            /* v8 ignore start */
            onMouseEnter={() => s.setActiveIndex(i)}
            onMouseLeave={() => s.setActiveIndex(-1)}
            /* v8 ignore stop */
          >
            {r.displayName}
          </button>
        ))}
        </div>
      </div>
    </>
  );
}

/** Dropdown open/close animation duration (ms). */
const DROPDOWN_ANIM_MS = 300;
const DROPDOWN_TRANSITION = `opacity ${DROPDOWN_ANIM_MS}ms ease, transform ${DROPDOWN_ANIM_MS}ms ease`;

function useStyles() {
  return useMemo(() => {
    const result: CSSProperties = {
      '--frosted-card': '1',
      display: Display.block,
      width: '100%',
      padding: padding(Gap.xl, Gap.section),
      textAlign: TextAlign.left,
      color: Colors.textSecondary,
      fontSize: Font.md,
      background: 'none',
      border: 'none',
      cursor: Cursor.pointer,
      position: Position.relative,
      overflow: Overflow.hidden,
      boxShadow: 'inset 0 0 0 100vmax rgba(255, 255, 255, calc(0.03 * var(--glow-hover, 0)))',
    } as CSSProperties;

    const dropdownBase: CSSProperties = {
      ...frostedCardSurface,
      backgroundColor: 'rgba(18,24,38,0.94)',
      position: Position.absolute,
      top: `calc(100% + ${Gap.xs}px)`,
      left: 0,
      right: 0,
      borderRadius: Radius.sm,
      zIndex: ZIndex.modalOverlay,
      height: Layout.searchDropdownMaxHeight,
      overflow: Overflow.hidden,
      overflowY: Overflow.auto,
      display: Display.flex,
      flexDirection: 'column' as CSSProperties['flexDirection'],
      transformOrigin: 'top right',
      transition: DROPDOWN_TRANSITION,
    };

    return {
      backdrop: {
        position: Position.fixed,
        top: 0,
        left: 0,
        right: 0,
        bottom: 0,
        zIndex: ZIndex.searchDropdown,
        background: CssValue.transparent,
      } as CSSProperties,
      container: {
        position: Position.relative,
        zIndex: ZIndex.searchDropdown + 1,
      } as CSSProperties,
      dropdownOpen: {
        ...dropdownBase,
        opacity: 1,
        transform: 'scale(1) translateY(0)',
        pointerEvents: PointerEvents.auto,
      } as CSSProperties,
      dropdownClosed: {
        ...dropdownBase,
        opacity: 0,
        transform: 'scale(0.95) translateY(-8px)',
        pointerEvents: PointerEvents.none,
      } as CSSProperties,
      spinnerWrap: {
        display: Display.flex,
        alignItems: Align.center,
        justifyContent: Justify.center,
        flex: 1,
      } as CSSProperties,
      hint: {
        display: Display.flex,
        alignItems: Align.center,
        justifyContent: Justify.center,
        flex: 1,
        color: Colors.textTertiary,
        fontSize: Font.md,
        textAlign: TextAlign.center,
      } as CSSProperties,
      hintAnimated: {
        display: Display.flex,
        alignItems: Align.center,
        justifyContent: Justify.center,
        flex: 1,
        color: Colors.textTertiary,
        fontSize: Font.md,
        textAlign: TextAlign.center,
        opacity: 0,
        animation: 'fadeInUp 300ms ease-out forwards',
      } as CSSProperties,
      result,
      resultActive: {
        ...result,
        backgroundColor: Colors.surfaceSubtle,
      } as CSSProperties,
    };
  }, []);
}
