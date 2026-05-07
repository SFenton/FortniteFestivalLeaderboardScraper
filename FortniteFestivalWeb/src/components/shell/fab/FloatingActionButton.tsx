/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useState, useEffect, useCallback, useRef, useMemo, type CSSProperties, type MouseEvent as ReactMouseEvent, type PointerEvent as ReactPointerEvent, type TouchEvent as ReactTouchEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { IoMenu } from 'react-icons/io5';
import { useScrollContainer } from '../../../contexts/ScrollContainerContext';
import { useSearchQuery } from '../../../contexts/SearchQueryContext';
import { Colors, Gap, Radius, Layout, MaxWidth, Shadow, ZIndex, Align, Position, Cursor, BoxSizing, IconSize, PointerEvents, CssValue, FAB_DISMISS_MS, QUICK_FADE_MS, frostedCard, purpleGlass, flexColumn, flexCenter, flexRow, padding } from '@festival/theme';
import { safeAreaBottomOffset } from '../../../utils/safeAreaStyles';
import { useIOSKeyboardPanGuard } from '../../../hooks/ui/useIOSKeyboardPanGuard';
import SearchBar, { type SearchBarRef } from '../../common/SearchBar';
import FABMenu from './FABMenu';

const KEYBOARD_CLEARANCE = 12;

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
  ariaLabel?: string;
  actionGroups?: ActionItem[][];
  directAction?: boolean;
  onPress: () => void;
}

export default function FloatingActionButton({
  mode: _mode,
  defaultOpen,
  placeholder,
  icon,
  ariaLabel,
  actionGroups,
  directAction,
  onPress,
}: Props) {
  const { t } = useTranslation();
  const searchVisible = !!defaultOpen;
  const [actionsOpen, setActionsOpen] = useState(false);
  const [popupMounted, setPopupMounted] = useState(false);
  const [popupVisible, setPopupVisible] = useState(false);
  const [searchFocused, setSearchFocused] = useState(false);
  const [keyboardInset, setKeyboardInset] = useState(0);
  const scrollContainerRef = useScrollContainer();
  const searchGuardActive = searchVisible && searchFocused;
  useIOSKeyboardPanGuard({ active: searchGuardActive, mode: 'floating-page', scrollContainerRef });

  /* v8 ignore start — action menu open/close handlers (rAF/setTimeout) */
  const openActions = useCallback(() => {
    setActionsOpen(true);
    setPopupMounted(true);
    requestAnimationFrame(() => requestAnimationFrame(() => setPopupVisible(true)));
  }, []);

  const closeActions = useCallback(() => {
    setPopupVisible(false);
    setActionsOpen(false);
    setTimeout(() => { setPopupMounted(false); }, FAB_DISMISS_MS);
  }, []);

  const handleFabPress = useCallback(() => {
    if (directAction) {
      onPress();
      return;
    }
    actionsOpen ? closeActions() : openActions();
  }, [actionsOpen, closeActions, directAction, onPress, openActions]);
  /* v8 ignore stop */

  const searchQuery = useSearchQuery();

  const containerRef = useRef<HTMLDivElement>(null);
  const searchOuterRef = useRef<HTMLDivElement>(null);
  const fabContainerRef = useRef<HTMLDivElement>(null);
  const searchInputRef = useRef<SearchBarRef>(null);
  const keyboardBaselineRef = useRef<number | null>(null);
  const keyboardInsetRef = useRef(0);

  const captureKeyboardBaseline = useCallback(() => {
    const visualViewport = window.visualViewport;
    const visualViewportBottom = visualViewport ? visualViewport.height + visualViewport.offsetTop : 0;
    keyboardBaselineRef.current = Math.max(
      keyboardBaselineRef.current ?? 0,
      window.innerHeight || 0,
      document.documentElement.clientHeight || 0,
      visualViewportBottom,
    );
  }, []);

  const clearKeyboardStateSoon = useCallback(() => {
    window.setTimeout(() => {
      keyboardBaselineRef.current = null;
      keyboardInsetRef.current = 0;
      setKeyboardInset(0);
    }, QUICK_FADE_MS);
  }, []);

  const focusSearchWithoutScroll = useCallback(() => {
    captureKeyboardBaseline();
    setSearchFocused(true);
    searchInputRef.current?.focus({ preventScroll: true });
  }, [captureKeyboardBaseline]);

  const handleSearchPressStart = useCallback((event: ReactPointerEvent<HTMLDivElement> | ReactTouchEvent<HTMLDivElement> | ReactMouseEvent<HTMLDivElement>) => {
    if (document.activeElement === event.target) return;
    event.preventDefault();
    event.stopPropagation();
    focusSearchWithoutScroll();
  }, [focusSearchWithoutScroll]);

  const updateKeyboardInset = useCallback(() => {
    if (!searchGuardActive) {
      keyboardInsetRef.current = 0;
      setKeyboardInset(0);
      return;
    }
    const visualViewport = window.visualViewport;
    if (!visualViewport) {
      setKeyboardInset(0);
      return;
    }
    captureKeyboardBaseline();
    const baseline = keyboardBaselineRef.current ?? window.innerHeight;
    const visibleBottom = visualViewport.height + visualViewport.offsetTop;
    const visualViewportLoss = baseline - visibleBottom;
    const innerHeightLoss = baseline - window.innerHeight;
    const viewportLoss = Math.max(0, Math.round(visualViewportLoss), Math.round(innerHeightLoss));
    const desiredBottom = visibleBottom - KEYBOARD_CLEARANCE;
    const currentInset = keyboardInsetRef.current;
    const rawBottoms = [searchOuterRef.current, fabContainerRef.current]
      .map(el => {
        const rect = el?.getBoundingClientRect();
        return rect && rect.bottom > 0 ? rect.bottom + currentInset : null;
      })
      .filter((bottom): bottom is number => bottom !== null);
    const measuredInset = rawBottoms.length > 0
      ? Math.max(0, ...rawBottoms.map(bottom => Math.ceil(bottom - desiredBottom)))
      : viewportLoss;
    const nextInset = Math.min(viewportLoss, measuredInset);
    keyboardInsetRef.current = nextInset;
    setKeyboardInset(nextInset);
  }, [captureKeyboardBaseline, searchGuardActive]);

  useEffect(() => {
    updateKeyboardInset();
    if (!searchGuardActive) return;
    const visualViewport = window.visualViewport;
    visualViewport?.addEventListener('resize', updateKeyboardInset);
    visualViewport?.addEventListener('scroll', updateKeyboardInset);
    window.addEventListener('resize', updateKeyboardInset);
    return () => {
      visualViewport?.removeEventListener('resize', updateKeyboardInset);
      visualViewport?.removeEventListener('scroll', updateKeyboardInset);
      window.removeEventListener('resize', updateKeyboardInset);
    };
  }, [searchGuardActive, updateKeyboardInset]);

  /* v8 ignore start — click-outside handler */
  useEffect(() => {
    if (!actionsOpen) return;
    const handler = (e: MouseEvent) => {
      if (containerRef.current && !containerRef.current.contains(e.target as Node)) {
        e.preventDefault();
        e.stopPropagation();
        closeActions();
      }
    };
    document.addEventListener('click', handler, true);
    return () => document.removeEventListener('click', handler, true);
  }, [actionsOpen, closeActions]);
  /* v8 ignore stop */

  const s = useFABStyles();
  const keyboardTransform = keyboardInset > 0 ? `translate3d(0, -${keyboardInset}px, 0)` : undefined;
  const keyboardTransition = `transform ${QUICK_FADE_MS}ms ease`;

  return (
    <div ref={containerRef}>
      {searchVisible && (
        <div
          ref={searchOuterRef}
          style={{ ...s.searchBarOuter, transform: keyboardTransform, transition: keyboardTransition }}
          onPointerDownCapture={handleSearchPressStart}
          onTouchStartCapture={handleSearchPressStart}
          onMouseDownCapture={handleSearchPressStart}
          onClickCapture={handleSearchPressStart}
        >
          <div className="fab-search-bar" style={s.searchBar}>
            <SearchBar
              ref={searchInputRef}
              value={searchQuery.query}
              onChange={searchQuery.setQuery}
              onKeyDown={e => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur(); }}
              onFocus={() => { captureKeyboardBaseline(); setSearchFocused(true); }}
              onBlur={() => { setSearchFocused(false); clearKeyboardStateSoon(); }}
              placeholder={placeholder ?? t('songs.searchPlaceholder')}
              enterKeyHint="search"
              style={s.searchInputWrap}
            />
          </div>
        </div>
      )}
      <div ref={fabContainerRef} style={{ ...s.container, transform: keyboardTransform, transition: keyboardTransition }}>
        <button
          style={s.fab}
          /* v8 ignore start -- action toggle */
          onClick={handleFabPress}
          /* v8 ignore stop */
          aria-label={ariaLabel ?? t('common.actions')}
        >
          {icon ?? <IoMenu size={IconSize.md} />}
        </button>
        {!directAction && popupMounted && (
          <FABMenu
            groups={actionGroups ?? []}
            visible={popupVisible}
            onAction={(action) => { closeActions(); action.onPress(); }}
          />
        )}
      </div>
    </div>
  );
}

function useFABStyles() {
  return useMemo(() => ({
    container: {
      position: Position.fixed,
      bottom: safeAreaBottomOffset(Layout.fabBottom),
      right: Layout.paddingHorizontal,
      ...flexColumn,
      alignItems: Align.end,
      gap: Gap.md,
      zIndex: ZIndex.popover,
      pointerEvents: PointerEvents.none,
    } as CSSProperties,
    fab: {
      ...purpleGlass,
      width: Layout.fabSize,
      height: Layout.fabSize,
      borderRadius: Radius.full,
      color: Colors.textPrimary,
      ...flexCenter,
      cursor: Cursor.pointer,
      flexShrink: 0,
      pointerEvents: PointerEvents.auto,
    } as CSSProperties,
    searchBarOuter: {
      position: Position.fixed,
      bottom: safeAreaBottomOffset(Layout.fabBottom),
      left: Gap.none,
      right: Gap.none,
      maxWidth: MaxWidth.card,
      margin: CssValue.marginCenter,
      padding: padding(0, Layout.paddingHorizontal),
      boxSizing: BoxSizing.borderBox,
      zIndex: ZIndex.popover,
      pointerEvents: PointerEvents.none,
    } as CSSProperties,
    searchBar: {
      ...flexColumn,
      gap: Gap.sm,
      position: Position.relative,
      pointerEvents: PointerEvents.auto,
    } as CSSProperties,
    searchInputWrap: {
      ...frostedCard,
      ...flexRow,
      gap: Gap.sm,
      width: CssValue.full,
      height: Layout.fabSize,
      padding: padding(0, Gap.section),
      borderRadius: Radius.full,
      boxSizing: BoxSizing.borderBox,
      boxShadow: Shadow.tooltip,
      cursor: Cursor.text,
    } as CSSProperties,
  }), []);
}