/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useState, useEffect, useCallback, useRef, useMemo, useLayoutEffect, type CSSProperties, type MouseEvent as ReactMouseEvent, type PointerEvent as ReactPointerEvent, type TouchEvent as ReactTouchEvent } from 'react';
import { flushSync } from 'react-dom';
import { useTranslation } from 'react-i18next';
import { IoClose, IoMenu, IoSearch } from 'react-icons/io5';
import { useScrollContainer } from '../../../contexts/ScrollContainerContext';
import { useSearchQuery } from '../../../contexts/SearchQueryContext';
import { Colors, Gap, Radius, Layout, MaxWidth, Shadow, ZIndex, Align, Position, Cursor, BoxSizing, IconSize, PointerEvents, Overflow, CssValue, Font, Isolation, FAB_DISMISS_MS, QUICK_FADE_MS, frostedCard, purpleGlass, flexColumn, flexCenter, flexRow, padding, scale } from '@festival/theme';
import { safeAreaBottomOffset } from '../../../utils/safeAreaStyles';
import { useIOSKeyboardPanGuard } from '../../../hooks/ui/useIOSKeyboardPanGuard';
import { SONGS_FAB_KEYBOARD_INSET_VAR, SONGS_FAB_KEYBOARD_OCCLUDED_BOTTOM_VAR } from '../../../constants/keyboardLayoutVars';
import SearchBar, { type SearchBarRef } from '../../common/SearchBar';
import FABMenu from './FABMenu';

const KEYBOARD_CLEARANCE = 12;
const DOCK_ACTION_FADE_MS = 280;
const DOCK_SEARCH_EXPAND_MS = 360;
const DOCK_LABEL_MIN_GAP = Gap.sm;
const DOCK_LABEL_ICON_GAP = Gap.md;
const DOCK_LABEL_HORIZONTAL_PADDING = Gap.xl;
const LABELED_FAB_ICON_LEFT_PADDING = Math.round((Layout.fabSize - IconSize.fab) / 2) - 1;
const LABELED_FAB_TEXT_SIDE_PADDING = LABELED_FAB_ICON_LEFT_PADDING;

export interface ActionItem {
  label: string;
  displayLabel?: string;
  active?: boolean;
  iconOnly?: boolean;
  tone?: 'default' | 'accent' | 'pulse';
  href?: string;
  target?: string;
  rel?: string;
  className?: string;
  icon: React.ReactNode;
  onPress: () => void;
}

interface Props {
  mode: 'players' | 'songs';
  defaultOpen?: boolean;
  placeholder?: string;
  icon?: React.ReactNode;
  label?: string;
  ariaLabel?: string;
  actionGroups?: ActionItem[][];
  dockActions?: ActionItem[];
  sideActions?: ActionItem[];
  directAction?: boolean;
  onPress: () => void;
}

type SearchGestureEvent = ReactPointerEvent<HTMLDivElement> | ReactTouchEvent<HTMLDivElement> | ReactMouseEvent<HTMLDivElement>;
type DockActionsPhase = 'visible' | 'fadingOut' | 'collapsed' | 'expandingIn';

interface DockLabelLayout {
  showLabels: boolean;
  searchWidth: number;
  searchTargetWidth: number;
  actionWidths: number[];
}

const DEFAULT_DOCK_LABEL_LAYOUT: DockLabelLayout = {
  showLabels: false,
  searchWidth: Layout.fabSize,
  searchTargetWidth: Layout.fabSize,
  actionWidths: [],
};

export default function FloatingActionButton({
  mode,
  defaultOpen,
  placeholder,
  icon,
  label,
  ariaLabel,
  actionGroups,
  dockActions,
  sideActions,
  directAction,
  onPress,
}: Props) {
  const { t } = useTranslation();
  const searchVisible = !!defaultOpen;
  const [actionsOpen, setActionsOpen] = useState(false);
  const [popupMounted, setPopupMounted] = useState(false);
  const [popupVisible, setPopupVisible] = useState(false);
  const [searchExpanded, setSearchExpanded] = useState(false);
  const [searchInputMounted, setSearchInputMounted] = useState(false);
  const [searchFieldContentVisible, setSearchFieldContentVisible] = useState(false);
  const [dockActionsPhase, setDockActionsPhase] = useState<DockActionsPhase>('visible');
  const [dockLabelLayout, setDockLabelLayout] = useState<DockLabelLayout>(DEFAULT_DOCK_LABEL_LAYOUT);
  const [dockLayoutReady, setDockLayoutReady] = useState(false);
  const [searchFocused, setSearchFocused] = useState(false);
  const [keyboardInset, setKeyboardInset] = useState(0);
  const scrollContainerRef = useScrollContainer();
  const useSongsDock = mode === 'songs' && searchVisible && dockActions != null;
  const hasSideActions = (sideActions?.length ?? 0) > 0;
  const hasMenuActions = (actionGroups ?? []).some(group => group.length > 0);
  const hasMainFab = !hasSideActions || directAction || searchVisible || hasMenuActions;
  const hasDockMainFab = directAction || hasMenuActions;
  const dockActionCount = dockActions?.length ?? 0;
  const dockMeasurementSignature = (dockActions ?? [])
    .map(action => `${action.label}\u001f${action.displayLabel ?? ''}`)
    .join('\u001e');
  const searchGuardActive = searchVisible && searchFocused;
  const isSongsSearch = mode === 'songs' && searchVisible;
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
  const dockStageRef = useRef<HTMLDivElement>(null);
  const dockLabelMeasureRef = useRef<HTMLDivElement>(null);
  const searchOuterRef = useRef<HTMLDivElement>(null);
  const fabContainerRef = useRef<HTMLDivElement>(null);
  const searchInputRef = useRef<SearchBarRef>(null);
  const keyboardBaselineRef = useRef<number | null>(null);
  const keyboardInsetRef = useRef(0);
  const searchFocusPendingRef = useRef(false);
  const searchFocusCompletedRef = useRef(false);
  const dockTransitionTimeoutsRef = useRef<number[]>([]);
  const dockLayoutRevealFramesRef = useRef<number[]>([]);

  const clearDockTransitionTimeouts = useCallback(() => {
    for (const timeoutId of dockTransitionTimeoutsRef.current) {
      window.clearTimeout(timeoutId);
    }
    dockTransitionTimeoutsRef.current = [];
  }, []);

  const clearDockLayoutRevealFrames = useCallback(() => {
    for (const frameId of dockLayoutRevealFramesRef.current) {
      window.cancelAnimationFrame(frameId);
    }
    dockLayoutRevealFramesRef.current = [];
  }, []);

  const revealDockLayout = useCallback(() => {
    clearDockLayoutRevealFrames();
    let firstFrameId = 0;
    firstFrameId = window.requestAnimationFrame(() => {
      dockLayoutRevealFramesRef.current = dockLayoutRevealFramesRef.current.filter(frameId => frameId !== firstFrameId);
      let secondFrameId = 0;
      secondFrameId = window.requestAnimationFrame(() => {
        dockLayoutRevealFramesRef.current = dockLayoutRevealFramesRef.current.filter(frameId => frameId !== secondFrameId);
        setDockLayoutReady(true);
      });
      dockLayoutRevealFramesRef.current.push(secondFrameId);
    });
    dockLayoutRevealFramesRef.current.push(firstFrameId);
  }, [clearDockLayoutRevealFrames]);

  const scheduleDockTransition = useCallback((callback: () => void, delayMs: number) => {
    const timeoutId = window.setTimeout(() => {
      dockTransitionTimeoutsRef.current = dockTransitionTimeoutsRef.current.filter(currentId => currentId !== timeoutId);
      callback();
    }, delayMs);
    dockTransitionTimeoutsRef.current.push(timeoutId);
  }, []);

  useEffect(() => () => clearDockTransitionTimeouts(), [clearDockTransitionTimeouts]);
  useEffect(() => () => clearDockLayoutRevealFrames(), [clearDockLayoutRevealFrames]);

  const updateDockLabelLayout = useCallback(() => {
    if (!useSongsDock) {
      setDockLabelLayout(DEFAULT_DOCK_LABEL_LAYOUT);
      setDockLayoutReady(false);
      return;
    }

    const stage = dockStageRef.current;
    const measure = dockLabelMeasureRef.current;
    if (!stage || !measure) {
      revealDockLayout();
      return;
    }

    const stageWidth = Math.floor(stage.getBoundingClientRect().width || stage.clientWidth || 0);
    const measuredControls = Array.from(measure.querySelectorAll<HTMLElement>('[data-dock-label-measure="control"]'));
    if (stageWidth <= 0 || measuredControls.length === 0) {
      setDockLabelLayout(DEFAULT_DOCK_LABEL_LAYOUT);
      revealDockLayout();
      return;
    }

    const measuredWidths = measuredControls.map(control => Math.ceil(control.getBoundingClientRect().width || control.offsetWidth || Layout.fabSize));
    const searchWidth = Math.max(Layout.fabSize, measuredWidths[0] ?? Layout.fabSize);
    const actionWidths = Array.from({ length: dockActionCount }, () => Layout.fabSize);
    const visibleControlCount = 1 + actionWidths.length;
    const actionWidthsTotal = actionWidths.reduce((total, width) => total + width, 0);
    const labelGapTotal = DOCK_LABEL_MIN_GAP * visibleControlCount;
    const availableSearchWidth = stageWidth
      - actionWidthsTotal
      - (hasDockMainFab ? Layout.fabSize : 0)
      - labelGapTotal;
    const showLabels = availableSearchWidth >= searchWidth;
    const nextLayout = {
      showLabels,
      searchWidth,
      searchTargetWidth: Math.max(Layout.fabSize, availableSearchWidth),
      actionWidths,
    };

    setDockLabelLayout(previous => {
      if (
        previous.showLabels === nextLayout.showLabels
        && previous.searchWidth === nextLayout.searchWidth
        && previous.searchTargetWidth === nextLayout.searchTargetWidth
        && previous.actionWidths.length === nextLayout.actionWidths.length
        && previous.actionWidths.every((width, index) => width === nextLayout.actionWidths[index])
      ) {
        return previous;
      }
      return nextLayout;
    });
    revealDockLayout();
  }, [dockActionCount, hasDockMainFab, revealDockLayout, useSongsDock]);

  useLayoutEffect(() => {
    if (!useSongsDock) {
      clearDockLayoutRevealFrames();
      setDockLabelLayout(DEFAULT_DOCK_LABEL_LAYOUT);
      setDockLayoutReady(false);
      return undefined;
    }

    clearDockLayoutRevealFrames();
    setDockLayoutReady(false);
    updateDockLabelLayout();
    const stage = dockStageRef.current;
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(updateDockLabelLayout);
    if (stage) observer?.observe(stage);
    window.addEventListener('resize', updateDockLabelLayout);
    return () => {
      observer?.disconnect();
      window.removeEventListener('resize', updateDockLabelLayout);
    };
  }, [clearDockLayoutRevealFrames, dockActions?.length, dockMeasurementSignature, updateDockLabelLayout, useSongsDock]);

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

  const expandSearch = useCallback(() => {
    captureKeyboardBaseline();
    clearDockTransitionTimeouts();

    if (!useSongsDock) {
      setSearchInputMounted(true);
      setSearchExpanded(true);
      setSearchFieldContentVisible(true);
      requestAnimationFrame(() => requestAnimationFrame(() => focusSearchWithoutScroll()));
      return;
    }

    setSearchFieldContentVisible(false);
    flushSync(() => {
      setSearchInputMounted(true);
    });
    focusSearchWithoutScroll();
    setDockActionsPhase('fadingOut');
    scheduleDockTransition(() => {
      setDockActionsPhase('collapsed');
      setSearchExpanded(true);
      scheduleDockTransition(() => {
        setSearchFieldContentVisible(true);
        focusSearchWithoutScroll();
      }, DOCK_SEARCH_EXPAND_MS);
    }, DOCK_ACTION_FADE_MS);
  }, [captureKeyboardBaseline, clearDockTransitionTimeouts, focusSearchWithoutScroll, scheduleDockTransition, useSongsDock]);

  const compactSearch = useCallback(() => {
    clearDockTransitionTimeouts();
    searchFocusPendingRef.current = false;
    searchFocusCompletedRef.current = false;
    setSearchFocused(false);

    if (useSongsDock) {
      setSearchFieldContentVisible(false);
      scheduleDockTransition(() => {
        setSearchExpanded(false);
        setDockActionsPhase('expandingIn');
        scheduleDockTransition(() => {
          setSearchInputMounted(false);
          scheduleDockTransition(() => setDockActionsPhase('visible'), 20);
        }, DOCK_SEARCH_EXPAND_MS);
      }, DOCK_ACTION_FADE_MS);
    }

    clearKeyboardStateSoon();
  }, [clearDockTransitionTimeouts, clearKeyboardStateSoon, scheduleDockTransition, useSongsDock]);

  const isSearchInputFocused = useCallback(() => {
    const activeElement = document.activeElement;
    return activeElement instanceof HTMLInputElement && !!searchOuterRef.current?.contains(activeElement);
  }, []);

  const stopSearchNativeGesture = useCallback((event: SearchGestureEvent) => {
    event.preventDefault();
    event.stopPropagation();
  }, []);

  const isClearSearchGesture = useCallback((event: SearchGestureEvent) => (
    event.target instanceof Element && event.target.closest('[data-fab-search-clear="true"]') != null
  ), []);

  const handleSearchPressStart = useCallback((event: SearchGestureEvent) => {
    if (isClearSearchGesture(event)) return;

    if (isSearchInputFocused()) {
      searchFocusPendingRef.current = false;
      searchFocusCompletedRef.current = true;
      stopSearchNativeGesture(event);
      return;
    }

    captureKeyboardBaseline();
    searchFocusPendingRef.current = true;
    searchFocusCompletedRef.current = false;
    stopSearchNativeGesture(event);
  }, [captureKeyboardBaseline, isClearSearchGesture, isSearchInputFocused, stopSearchNativeGesture]);

  const handleSearchPressEnd = useCallback((event: SearchGestureEvent) => {
    if (isClearSearchGesture(event)) return;

    if (isSearchInputFocused()) {
      searchFocusPendingRef.current = false;
      searchFocusCompletedRef.current = true;
      stopSearchNativeGesture(event);
      return;
    }

    if (searchFocusCompletedRef.current) {
      stopSearchNativeGesture(event);
      return;
    }

    if (!searchFocusPendingRef.current && event.type !== 'click') return;

    searchFocusPendingRef.current = false;
    searchFocusCompletedRef.current = true;
    stopSearchNativeGesture(event);
    focusSearchWithoutScroll();
  }, [focusSearchWithoutScroll, isClearSearchGesture, isSearchInputFocused, stopSearchNativeGesture]);

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
    const previousInset = keyboardInsetRef.current;
    const nextInset = Math.min(viewportLoss, measuredInset);
    keyboardInsetRef.current = nextInset;
    setKeyboardInset(nextInset);

    if (useSongsDock && searchFocused && previousInset > 0 && nextInset === 0) {
      window.setTimeout(() => searchInputRef.current?.blur(), 0);
    }
  }, [captureKeyboardBaseline, searchFocused, searchGuardActive, useSongsDock]);

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

  useEffect(() => {
    const root = document.documentElement;
    if (!isSongsSearch) {
      root.style.removeProperty(SONGS_FAB_KEYBOARD_INSET_VAR);
      root.style.removeProperty(SONGS_FAB_KEYBOARD_OCCLUDED_BOTTOM_VAR);
      return undefined;
    }

    root.style.setProperty(SONGS_FAB_KEYBOARD_INSET_VAR, `${keyboardInset}px`);
    root.style.setProperty(
      SONGS_FAB_KEYBOARD_OCCLUDED_BOTTOM_VAR,
      keyboardInset > 0 ? `calc(${keyboardInset}px + ${safeAreaBottomOffset(Layout.fabBottom + Layout.fabSize)})` : '0px',
    );

    return () => {
      root.style.removeProperty(SONGS_FAB_KEYBOARD_INSET_VAR);
      root.style.removeProperty(SONGS_FAB_KEYBOARD_OCCLUDED_BOTTOM_VAR);
    };
  }, [isSongsSearch, keyboardInset]);

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
  const keyboardOpen = keyboardInset > 0;
  const keyboardTransform = keyboardInset > 0 ? `translate3d(0, -${keyboardInset}px, 0)` : undefined;
  const keyboardTransition = `transform ${QUICK_FADE_MS}ms ease`;
  const searchInputWrapStyle = useSongsDock
    ? keyboardOpen ? s.dockSearchInputWrapKeyboard : s.dockSearchInputWrap
    : keyboardOpen ? s.searchInputWrapKeyboard : s.searchInputWrap;
  const hasSearchQuery = searchQuery.query.length > 0;
  const clearSearchLabel = t('common.clearSearch', 'Clear Search');
  const searchButtonLabel = t('common.searchAction');
  const dockLabelsEnabled = dockLabelLayout.showLabels;
  const dockSearchTextVisible = dockLabelsEnabled || hasSearchQuery;
  const dockSearchMinWidth = dockLabelsEnabled && !hasSearchQuery ? dockLabelLayout.searchWidth : Layout.fabSize;
  const dockSearchCollapsedWidth = dockSearchTextVisible ? dockLabelLayout.searchTargetWidth : Layout.fabSize;
  const gateDockInitialTransition = useCallback((style: CSSProperties): CSSProperties => (
    dockLayoutReady ? style : { ...style, transition: CssValue.none }
  ), [dockLayoutReady]);
  const dockSearchSlotStyle = gateDockInitialTransition(searchExpanded
    ? s.dockSearchExpanded
    : searchInputMounted
      ? { ...s.dockSearchCollapsing, width: dockSearchCollapsedWidth, flexBasis: dockSearchCollapsedWidth }
      : dockSearchTextVisible
        ? { ...s.dockSearchCollapsed, width: dockSearchCollapsedWidth, flexBasis: dockSearchCollapsedWidth, minWidth: dockSearchMinWidth }
        : { ...s.dockSearchCollapsed, width: dockSearchCollapsedWidth, flexBasis: dockSearchCollapsedWidth });
  const dockRowStyle = searchInputMounted ? s.dockRowExpanded : dockSearchTextVisible ? s.dockRowLabeled : s.dockRow;
  const dockVisibleContentStyle = dockLayoutReady ? s.dockVisibleContentReady : s.dockVisibleContentPending;
  const dockAnchorSpacerStyle = searchInputMounted ? s.dockAnchorSpacerExpanded : s.dockAnchorSpacer;
  const getDockActionSlotStyle = useCallback((actionIndex: number) => {
    const actionWidth = dockLabelsEnabled ? (dockLabelLayout.actionWidths[actionIndex] ?? Layout.fabSize) : Layout.fabSize;
    const sizedSlotStyle = { width: actionWidth, flex: `0 0 ${actionWidth}px` };
    if (dockActionsPhase === 'visible') return gateDockInitialTransition({ ...s.dockActionSlot, ...sizedSlotStyle });
    if (dockActionsPhase === 'fadingOut') return gateDockInitialTransition({ ...s.dockActionSlotFadingOut, ...sizedSlotStyle });
    if (dockActionsPhase === 'expandingIn') return gateDockInitialTransition({ ...s.dockActionSlotExpandingIn, ...sizedSlotStyle });
    return gateDockInitialTransition(s.dockActionSlotCollapsed);
  }, [dockActionsPhase, dockLabelLayout.actionWidths, dockLabelsEnabled, gateDockInitialTransition, s.dockActionSlot, s.dockActionSlotCollapsed, s.dockActionSlotExpandingIn, s.dockActionSlotFadingOut]);
  const dockFieldContentVisible = searchFieldContentVisible || hasSearchQuery;
  const searchFieldContentStyle = dockFieldContentVisible
    ? s.searchFieldContentVisible
    : s.searchFieldContentHidden;
  const collapsedSearchButtonStyle = dockSearchTextVisible
    ? { ...(hasSearchQuery ? s.searchPillActive : s.searchPill), width: CssValue.full, justifyContent: Align.start, overflow: Overflow.hidden }
    : s.searchCircle;
  const collapsedSearchText = hasSearchQuery ? searchQuery.query : searchButtonLabel;
  const collapsedSearchTextStyle = hasSearchQuery ? s.dockSearchQueryText : s.dockButtonLabel;
  const getDockActionButtonStyle = useCallback((active?: boolean) => (
    active ? s.fabActionCircleActive : s.fabActionCircle
  ), [s.fabActionCircle, s.fabActionCircleActive]);
  const getSideActionButtonStyle = useCallback((action: ActionItem) => {
    if (action.iconOnly) return action.active ? s.fabActionCircleActive : s.fabActionCircle;
    if (action.tone === 'pulse') return s.fabSideActionCirclePulse;
    if (action.tone === 'accent' || action.active) return s.fabSideActionCircleAccent;
    return s.fabSideActionCircle;
  }, [s.fabActionCircle, s.fabActionCircleActive, s.fabSideActionCircle, s.fabSideActionCircleAccent, s.fabSideActionCirclePulse]);

  const renderSideAction = useCallback((action: ActionItem) => {
    const content = action.iconOnly ? action.icon : (
      <>
        {action.icon}
        <span style={s.sideActionLabel}>{action.displayLabel ?? action.label}</span>
      </>
    );
    const style = getSideActionButtonStyle(action);
    const commonProps = {
      className: action.className,
      style,
      'aria-label': action.label,
      title: action.label,
      'data-testid': 'fab-side-action',
    };
    if (action.href) {
      return (
        <a
          key={action.label}
          {...commonProps}
          href={action.href}
          target={action.target}
          rel={action.rel}
          onClick={action.onPress}
        >
          {content}
        </a>
      );
    }
    return (
      <button
        key={action.label}
        type="button"
        {...commonProps}
        onClick={action.onPress}
      >
        {content}
      </button>
    );
  }, [getSideActionButtonStyle, s.sideActionLabel]);

  const clearSearchAndFocus = useCallback(() => {
    searchQuery.setQuery('');
    focusSearchWithoutScroll();
  }, [focusSearchWithoutScroll, searchQuery]);

  const handleClearSearchPressStart = useCallback((event: ReactPointerEvent<HTMLButtonElement> | ReactMouseEvent<HTMLButtonElement> | ReactTouchEvent<HTMLButtonElement>) => {
    event.preventDefault();
    event.stopPropagation();
    clearSearchAndFocus();
  }, [clearSearchAndFocus]);

  const handleClearSearch = useCallback((event: ReactMouseEvent<HTMLButtonElement>) => {
    event.preventDefault();
    event.stopPropagation();
    clearSearchAndFocus();
  }, [clearSearchAndFocus]);

  if (useSongsDock) {
    return (
      <div ref={containerRef}>
        <div className="fab-search-dock" style={{ ...s.dockOuter, transform: keyboardTransform, transition: keyboardTransition }}>
          <div ref={dockStageRef} style={s.dockStage} data-testid="fab-dock-stage">
            <div ref={dockLabelMeasureRef} style={s.dockLabelMeasure} aria-hidden="true">
              <div style={s.searchPill} data-dock-label-measure="control" data-dock-label-index="0">
                <IoSearch size={IconSize.fab} />
                <span style={s.dockButtonLabel}>{searchButtonLabel}</span>
              </div>
              {(dockActions ?? []).map((action, index) => (
                <div key={action.label} style={s.fabPill} data-dock-label-measure="control" data-dock-label-index={index + 1}>
                  {action.icon}
                  <span style={s.dockButtonLabel}>{action.displayLabel ?? action.label}</span>
                </div>
              ))}
            </div>
            <div style={dockVisibleContentStyle} data-testid="fab-dock-visible-content">
              <div style={dockRowStyle} data-testid="fab-dock-row">
                <div
                  ref={searchOuterRef}
                  style={dockSearchSlotStyle}
                  onPointerDownCapture={searchInputMounted ? handleSearchPressStart : undefined}
                  onPointerUpCapture={searchInputMounted ? handleSearchPressEnd : undefined}
                  onTouchStartCapture={searchInputMounted ? handleSearchPressStart : undefined}
                  onTouchEndCapture={searchInputMounted ? handleSearchPressEnd : undefined}
                  onMouseDownCapture={searchInputMounted ? handleSearchPressStart : undefined}
                  onMouseUpCapture={searchInputMounted ? handleSearchPressEnd : undefined}
                  onClickCapture={searchInputMounted ? handleSearchPressEnd : undefined}
                >
                  {searchInputMounted ? (
                    <SearchBar
                      ref={searchInputRef}
                      value={searchQuery.query}
                      onChange={searchQuery.setQuery}
                      onKeyDown={e => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur(); }}
                      onFocus={() => { captureKeyboardBaseline(); setSearchFocused(true); }}
                      onBlur={compactSearch}
                      placeholder={placeholder ?? t('songs.searchPlaceholder')}
                      enterKeyHint="search"
                      className="fab-search-bar"
                      style={searchInputWrapStyle}
                      iconStyle={s.searchFieldIconVisible}
                      iconSize={IconSize.fab}
                      inputStyle={searchFieldContentStyle}
                      autoFocus
                      trailing={hasSearchQuery ? (
                        <button
                          type="button"
                          style={dockFieldContentVisible ? s.clearSearchButton : s.clearSearchButtonHidden}
                          data-fab-search-clear="true"
                          aria-label={clearSearchLabel}
                          title={clearSearchLabel}
                          onPointerDown={handleClearSearchPressStart}
                          onMouseDown={handleClearSearchPressStart}
                          onTouchStart={handleClearSearchPressStart}
                          onClick={handleClearSearch}
                        >
                          <IoClose size={IconSize.sm} />
                        </button>
                      ) : null}
                    />
                  ) : (
                    <button
                      type="button"
                      style={collapsedSearchButtonStyle}
                      onClick={expandSearch}
                      aria-label={searchButtonLabel}
                      title={searchButtonLabel}
                      data-testid="fab-search-toggle"
                    >
                      <span style={s.dockSearchButtonIconVisible} data-testid="fab-search-toggle-icon">
                        <IoSearch size={IconSize.fab} />
                        {dockSearchTextVisible && <span style={collapsedSearchTextStyle}>{collapsedSearchText}</span>}
                      </span>
                    </button>
                  )}
                </div>
                {(dockActions ?? []).map((action, index) => (
                  <div key={action.label} style={getDockActionSlotStyle(index)}>
                    <button
                      type="button"
                      style={getDockActionButtonStyle(action.active)}
                      aria-label={action.label}
                      title={action.label}
                      onClick={action.onPress}
                    >
                      {action.icon}
                    </button>
                  </div>
                ))}
                {hasDockMainFab && <div style={dockAnchorSpacerStyle} aria-hidden="true" />}
              </div>
              {hasDockMainFab && (
                <div ref={fabContainerRef} style={s.dockFabSlot}>
                  <button
                    type="button"
                    style={label ? s.fabLabeled : s.fab}
                    onClick={handleFabPress}
                    aria-label={ariaLabel ?? t('common.actions')}
                    title={ariaLabel ?? t('common.actions')}
                  >
                    {icon ?? <IoMenu size={IconSize.md} />}
                    {label && <span style={s.fabLabel}>{label}</span>}
                  </button>
                  {!directAction && popupMounted && (
                    <FABMenu
                      groups={actionGroups ?? []}
                      visible={popupVisible}
                      onAction={(action) => { closeActions(); action.onPress(); }}
                    />
                  )}
                </div>
              )}
            </div>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div ref={containerRef}>
      {searchVisible && (
        <div
          ref={searchOuterRef}
          style={{ ...s.searchBarOuter, transform: keyboardTransform, transition: keyboardTransition }}
          onPointerDownCapture={handleSearchPressStart}
          onPointerUpCapture={handleSearchPressEnd}
          onTouchStartCapture={handleSearchPressStart}
          onTouchEndCapture={handleSearchPressEnd}
          onMouseDownCapture={handleSearchPressStart}
          onMouseUpCapture={handleSearchPressEnd}
          onClickCapture={handleSearchPressEnd}
        >
          <div className="fab-search-bar" style={s.searchBar}>
            <SearchBar
              ref={searchInputRef}
              value={searchQuery.query}
              onChange={searchQuery.setQuery}
              onKeyDown={e => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur(); }}
              onFocus={() => { captureKeyboardBaseline(); setSearchFocused(true); }}
              onBlur={compactSearch}
              placeholder={placeholder ?? t('songs.searchPlaceholder')}
              enterKeyHint="search"
              style={searchInputWrapStyle}
            />
          </div>
        </div>
      )}
      <div ref={fabContainerRef} style={{ ...(hasSideActions ? s.sideActionContainer : s.container), transform: keyboardTransform, transition: keyboardTransition }}>
        {hasSideActions && (
          <div style={s.sideActions} data-testid="fab-side-actions">
            {(sideActions ?? []).map(renderSideAction)}
          </div>
        )}
        {hasMainFab && (
          <button
            style={label ? s.fabLabeled : s.fab}
            /* v8 ignore start -- action toggle */
            onClick={handleFabPress}
            /* v8 ignore stop */
            aria-label={ariaLabel ?? t('common.actions')}
            title={ariaLabel ?? t('common.actions')}
          >
            {icon ?? <IoMenu size={IconSize.md} />}
            {label && <span style={s.fabLabel}>{label}</span>}
          </button>
        )}
        {hasMainFab && !directAction && popupMounted && (
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
    dockOuter: {
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
    dockStage: {
      position: Position.relative,
      width: CssValue.full,
      height: Layout.fabSize,
      pointerEvents: PointerEvents.none,
    } as CSSProperties,
    dockLabelMeasure: {
      position: Position.absolute,
      top: 0,
      left: 0,
      display: 'flex',
      alignItems: Align.center,
      gap: Gap.none,
      width: 'max-content',
      height: Layout.fabSize,
      visibility: 'hidden',
      opacity: 0,
      overflow: Overflow.hidden,
      pointerEvents: PointerEvents.none,
      zIndex: -1,
    } as CSSProperties,
    dockVisibleContentPending: {
      position: Position.relative,
      width: CssValue.full,
      height: CssValue.full,
      opacity: 0,
      pointerEvents: PointerEvents.none,
      transition: `opacity ${DOCK_ACTION_FADE_MS}ms ease`,
    } as CSSProperties,
    dockVisibleContentReady: {
      position: Position.relative,
      width: CssValue.full,
      height: CssValue.full,
      opacity: 1,
      pointerEvents: PointerEvents.none,
      transition: `opacity ${DOCK_ACTION_FADE_MS}ms ease`,
    } as CSSProperties,
    dockRow: {
      ...flexRow,
      alignItems: Align.center,
      justifyContent: 'space-between',
      gap: Gap.none,
      width: CssValue.full,
      height: CssValue.full,
      pointerEvents: PointerEvents.none,
    } as CSSProperties,
    dockRowLabeled: {
      ...flexRow,
      alignItems: Align.center,
      justifyContent: 'flex-start',
      gap: DOCK_LABEL_MIN_GAP,
      width: CssValue.full,
      height: CssValue.full,
      pointerEvents: PointerEvents.none,
    } as CSSProperties,
    dockRowExpanded: {
      ...flexRow,
      alignItems: Align.center,
      justifyContent: 'flex-start',
      gap: Gap.none,
      width: CssValue.full,
      height: CssValue.full,
      pointerEvents: PointerEvents.none,
    } as CSSProperties,
    dockSearchCollapsed: {
      width: Layout.fabSize,
      flexGrow: 0,
      flexShrink: 0,
      flexBasis: Layout.fabSize,
      marginRight: 0,
      overflow: 'visible',
      pointerEvents: PointerEvents.auto,
      transition: [
        `width ${DOCK_SEARCH_EXPAND_MS}ms ease`,
        `flex-grow ${DOCK_SEARCH_EXPAND_MS}ms ease`,
        `flex-basis ${DOCK_SEARCH_EXPAND_MS}ms ease`,
        `margin-right ${DOCK_SEARCH_EXPAND_MS}ms ease`,
        `opacity ${DOCK_SEARCH_EXPAND_MS}ms ease`,
      ].join(', '),
    } as CSSProperties,
    dockSearchCollapsing: {
      width: Layout.fabSize,
      flexGrow: 0,
      flexShrink: 0,
      flexBasis: Layout.fabSize,
      marginRight: 0,
      overflow: Overflow.hidden,
      pointerEvents: PointerEvents.auto,
      transition: [
        `width ${DOCK_SEARCH_EXPAND_MS}ms ease`,
        `flex-grow ${DOCK_SEARCH_EXPAND_MS}ms ease`,
        `flex-basis ${DOCK_SEARCH_EXPAND_MS}ms ease`,
        `margin-right ${DOCK_SEARCH_EXPAND_MS}ms ease`,
        `opacity ${DOCK_SEARCH_EXPAND_MS}ms ease`,
      ].join(', '),
    } as CSSProperties,
    dockSearchExpanded: {
      width: `calc(100% - ${Layout.fabSize + Gap.md}px)`,
      flexGrow: 0,
      flexShrink: 1,
      flexBasis: `calc(100% - ${Layout.fabSize + Gap.md}px)`,
      marginRight: 0,
      minWidth: 0,
      overflow: 'visible',
      pointerEvents: PointerEvents.auto,
      transition: [
        `width ${DOCK_SEARCH_EXPAND_MS}ms ease`,
        `flex-grow ${DOCK_SEARCH_EXPAND_MS}ms ease`,
        `flex-basis ${DOCK_SEARCH_EXPAND_MS}ms ease`,
        `margin-right ${DOCK_SEARCH_EXPAND_MS}ms ease`,
        `opacity ${DOCK_SEARCH_EXPAND_MS}ms ease`,
      ].join(', '),
    } as CSSProperties,
    dockActionSlot: {
      width: Layout.fabSize,
      flex: `0 0 ${Layout.fabSize}px`,
      marginLeft: 0,
      opacity: 1,
      transform: scale(1),
      overflow: 'visible',
      pointerEvents: PointerEvents.auto,
      transition: [
        `width ${DOCK_SEARCH_EXPAND_MS}ms ease`,
        `flex-basis ${DOCK_SEARCH_EXPAND_MS}ms ease`,
        `margin-left ${DOCK_SEARCH_EXPAND_MS}ms ease`,
        `opacity ${DOCK_ACTION_FADE_MS}ms ease`,
        `transform ${DOCK_ACTION_FADE_MS}ms ease`,
      ].join(', '),
    } as CSSProperties,
    dockActionSlotFadingOut: {
      width: Layout.fabSize,
      flex: `0 0 ${Layout.fabSize}px`,
      marginLeft: 0,
      opacity: 0,
      transform: scale(1),
      overflow: 'visible',
      pointerEvents: PointerEvents.none,
      transition: [
        `opacity ${DOCK_ACTION_FADE_MS}ms ease`,
        `transform ${DOCK_ACTION_FADE_MS}ms ease`,
      ].join(', '),
    } as CSSProperties,
    dockActionSlotCollapsed: {
      width: 0,
      flex: '0 0 0px',
      marginLeft: 0,
      opacity: 0,
      transform: scale(1),
      overflow: Overflow.hidden,
      pointerEvents: PointerEvents.none,
      transition: [
        `width ${DOCK_SEARCH_EXPAND_MS}ms ease`,
        `flex-basis ${DOCK_SEARCH_EXPAND_MS}ms ease`,
        `margin-left ${DOCK_SEARCH_EXPAND_MS}ms ease`,
      ].join(', '),
    } as CSSProperties,
    dockActionSlotExpandingIn: {
      width: Layout.fabSize,
      flex: `0 0 ${Layout.fabSize}px`,
      marginLeft: 0,
      opacity: 0,
      transform: scale(1),
      overflow: 'visible',
      pointerEvents: PointerEvents.none,
      transition: [
        `width ${DOCK_SEARCH_EXPAND_MS}ms ease`,
        `flex-basis ${DOCK_SEARCH_EXPAND_MS}ms ease`,
        `margin-left ${DOCK_SEARCH_EXPAND_MS}ms ease`,
      ].join(', '),
    } as CSSProperties,
    dockAnchorSpacer: {
      width: Layout.fabSize,
      flex: `0 0 ${Layout.fabSize}px`,
      marginLeft: 0,
      pointerEvents: PointerEvents.none,
    } as CSSProperties,
    dockAnchorSpacerExpanded: {
      width: Layout.fabSize,
      flex: `0 0 ${Layout.fabSize}px`,
      marginLeft: Gap.md,
      pointerEvents: PointerEvents.none,
    } as CSSProperties,
    dockFabSlot: {
      position: Position.absolute,
      top: 0,
      right: 0,
      width: Layout.fabSize,
      flex: `0 0 ${Layout.fabSize}px`,
      marginLeft: 0,
      pointerEvents: PointerEvents.auto,
    } as CSSProperties,
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
    sideActionContainer: {
      position: Position.fixed,
      bottom: safeAreaBottomOffset(Layout.fabBottom),
      right: Layout.paddingHorizontal,
      ...flexRow,
      alignItems: Align.center,
      justifyContent: Align.end,
      gap: Gap.md,
      maxWidth: `calc(100vw - ${Layout.paddingHorizontal * 2}px)`,
      zIndex: ZIndex.popover,
      pointerEvents: PointerEvents.none,
    } as CSSProperties,
    sideActions: {
      ...flexRow,
      alignItems: Align.center,
      justifyContent: Align.end,
      gap: Gap.md,
      minWidth: 0,
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
    fabLabeled: {
      ...purpleGlass,
      minWidth: Layout.fabSize,
      maxWidth: `calc(100vw - ${Layout.paddingHorizontal * 2}px)`,
      height: Layout.fabSize,
      borderRadius: Radius.full,
      color: Colors.textPrimary,
      ...flexCenter,
      justifyContent: Align.start,
      gap: DOCK_LABEL_ICON_GAP,
      padding: padding(0, LABELED_FAB_TEXT_SIDE_PADDING, 0, LABELED_FAB_ICON_LEFT_PADDING),
      boxSizing: BoxSizing.borderBox,
      cursor: Cursor.pointer,
      flexShrink: 0,
      pointerEvents: PointerEvents.auto,
      whiteSpace: 'nowrap',
      overflow: Overflow.hidden,
    } as CSSProperties,
    fabLabel: {
      minWidth: 0,
      overflow: Overflow.hidden,
      textOverflow: 'ellipsis',
      whiteSpace: 'nowrap',
      fontSize: 13,
      fontWeight: 700,
      lineHeight: 1,
      letterSpacing: 0,
    } as CSSProperties,
    fabActionCircle: {
      ...frostedCard,
      width: Layout.fabSize,
      height: Layout.fabSize,
      borderRadius: Radius.full,
      color: Colors.textPrimary,
      ...flexCenter,
      cursor: Cursor.pointer,
      flexShrink: 0,
      pointerEvents: PointerEvents.auto,
    } as CSSProperties,
    fabActionCircleActive: {
      ...frostedCard,
      width: Layout.fabSize,
      height: Layout.fabSize,
      borderRadius: Radius.full,
      color: Colors.textPrimary,
      ...flexCenter,
      cursor: Cursor.pointer,
      flexShrink: 0,
      pointerEvents: PointerEvents.auto,
      backgroundColor: Colors.accentBlue,
      backgroundImage: CssValue.none,
      border: '1px solid transparent',
      boxShadow: CssValue.none,
    } as CSSProperties,
    fabSideActionCircle: {
      ...purpleGlass,
      minWidth: Layout.fabSize,
      height: Layout.fabSize,
      borderRadius: Radius.full,
      color: Colors.textPrimary,
      textDecoration: CssValue.none,
      gap: DOCK_LABEL_ICON_GAP,
      padding: padding(0, DOCK_LABEL_HORIZONTAL_PADDING),
      boxSizing: BoxSizing.borderBox,
      whiteSpace: 'nowrap',
      overflow: Overflow.hidden,
      ...flexCenter,
      cursor: Cursor.pointer,
      flexShrink: 0,
      pointerEvents: PointerEvents.auto,
    } as CSSProperties,
    fabSideActionCircleAccent: {
      minWidth: Layout.fabSize,
      height: Layout.fabSize,
      borderRadius: Radius.full,
      backgroundColor: Colors.accentBlue,
      color: Colors.textPrimary,
      textDecoration: CssValue.none,
      border: CssValue.none,
      boxShadow: Shadow.tooltip,
      gap: DOCK_LABEL_ICON_GAP,
      padding: padding(0, DOCK_LABEL_HORIZONTAL_PADDING),
      boxSizing: BoxSizing.borderBox,
      whiteSpace: 'nowrap',
      overflow: Overflow.hidden,
      ...flexCenter,
      cursor: Cursor.pointer,
      flexShrink: 0,
      pointerEvents: PointerEvents.auto,
    } as CSSProperties,
    fabSideActionCirclePulse: {
      minWidth: Layout.fabSize,
      height: Layout.fabSize,
      borderRadius: Radius.full,
      backgroundColor: Colors.transparent,
      color: Colors.textPrimary,
      textDecoration: CssValue.none,
      border: CssValue.none,
      boxShadow: Shadow.tooltip,
      position: Position.relative,
      isolation: Isolation.isolate,
      gap: DOCK_LABEL_ICON_GAP,
      padding: padding(0, DOCK_LABEL_HORIZONTAL_PADDING),
      boxSizing: BoxSizing.borderBox,
      whiteSpace: 'nowrap',
      overflow: Overflow.hidden,
      ...flexCenter,
      cursor: Cursor.pointer,
      flexShrink: 0,
      pointerEvents: PointerEvents.auto,
    } as CSSProperties,
    sideActionLabel: {
      minWidth: 0,
      overflow: Overflow.hidden,
      textOverflow: 'ellipsis',
      whiteSpace: 'nowrap',
      fontSize: 13,
      fontWeight: 700,
      lineHeight: 1,
      letterSpacing: 0,
    } as CSSProperties,
    fabPill: {
      ...frostedCard,
      minWidth: Layout.fabSize,
      height: Layout.fabSize,
      borderRadius: Radius.full,
      color: Colors.textPrimary,
      ...flexCenter,
      gap: DOCK_LABEL_ICON_GAP,
      padding: padding(0, DOCK_LABEL_HORIZONTAL_PADDING),
      boxSizing: BoxSizing.borderBox,
      cursor: Cursor.pointer,
      flexShrink: 0,
      pointerEvents: PointerEvents.auto,
      whiteSpace: 'nowrap',
    } as CSSProperties,
    fabPillActive: {
      ...frostedCard,
      minWidth: Layout.fabSize,
      height: Layout.fabSize,
      borderRadius: Radius.full,
      color: Colors.textPrimary,
      ...flexCenter,
      gap: DOCK_LABEL_ICON_GAP,
      padding: padding(0, DOCK_LABEL_HORIZONTAL_PADDING),
      boxSizing: BoxSizing.borderBox,
      cursor: Cursor.pointer,
      flexShrink: 0,
      pointerEvents: PointerEvents.auto,
      whiteSpace: 'nowrap',
      backgroundColor: Colors.accentBlue,
      backgroundImage: CssValue.none,
      border: '1px solid transparent',
      boxShadow: CssValue.none,
    } as CSSProperties,
    searchCircle: {
      ...frostedCard,
      width: Layout.fabSize,
      height: Layout.fabSize,
      borderRadius: Radius.full,
      color: Colors.textPrimary,
      boxShadow: Shadow.tooltip,
      opacity: 0.9,
      ...flexCenter,
      cursor: Cursor.pointer,
      position: Position.relative,
      pointerEvents: PointerEvents.auto,
    } as CSSProperties,
    searchCircleActive: {
      ...frostedCard,
      width: Layout.fabSize,
      height: Layout.fabSize,
      borderRadius: Radius.full,
      color: Colors.textPrimary,
      boxShadow: `${Shadow.tooltip}, 0 0 0 1px ${Colors.purpleHighlightBorder}`,
      opacity: 1,
      ...flexCenter,
      cursor: Cursor.pointer,
      position: Position.relative,
      pointerEvents: PointerEvents.auto,
    } as CSSProperties,
    searchPill: {
      ...frostedCard,
      minWidth: Layout.fabSize,
      height: Layout.fabSize,
      borderRadius: Radius.full,
      color: Colors.textPrimary,
      boxShadow: Shadow.tooltip,
      opacity: 0.9,
      ...flexCenter,
      gap: DOCK_LABEL_ICON_GAP,
      padding: padding(0, DOCK_LABEL_HORIZONTAL_PADDING),
      boxSizing: BoxSizing.borderBox,
      cursor: Cursor.pointer,
      position: Position.relative,
      pointerEvents: PointerEvents.auto,
      whiteSpace: 'nowrap',
    } as CSSProperties,
    searchPillActive: {
      ...frostedCard,
      minWidth: Layout.fabSize,
      height: Layout.fabSize,
      borderRadius: Radius.full,
      color: Colors.textPrimary,
      boxShadow: `${Shadow.tooltip}, 0 0 0 1px ${Colors.purpleHighlightBorder}`,
      opacity: 1,
      ...flexCenter,
      gap: DOCK_LABEL_ICON_GAP,
      padding: padding(0, DOCK_LABEL_HORIZONTAL_PADDING),
      boxSizing: BoxSizing.borderBox,
      cursor: Cursor.pointer,
      position: Position.relative,
      pointerEvents: PointerEvents.auto,
      whiteSpace: 'nowrap',
    } as CSSProperties,
    dockSearchButtonIconVisible: {
      ...flexCenter,
      gap: DOCK_LABEL_ICON_GAP,
      minWidth: 0,
      maxWidth: CssValue.full,
      opacity: 1,
      transition: `opacity ${DOCK_ACTION_FADE_MS}ms ease`,
      pointerEvents: PointerEvents.none,
    } as CSSProperties,
    dockButtonLabel: {
      fontSize: 13,
      fontWeight: 700,
      lineHeight: 1,
      letterSpacing: 0,
      whiteSpace: 'nowrap',
    } as CSSProperties,
    dockSearchQueryText: {
      minWidth: 0,
      overflow: Overflow.hidden,
      textOverflow: 'ellipsis',
      whiteSpace: 'nowrap',
      fontSize: Font.md,
      fontWeight: 400,
      lineHeight: 1,
      letterSpacing: 0,
    } as CSSProperties,
    searchFieldIconVisible: {
      opacity: 1,
      flexShrink: 0,
    } as CSSProperties,
    searchFieldContentVisible: {
      opacity: 1,
      transition: `opacity ${DOCK_ACTION_FADE_MS}ms ease`,
    } as CSSProperties,
    searchFieldContentHidden: {
      opacity: 0,
      transition: `opacity ${DOCK_ACTION_FADE_MS}ms ease`,
    } as CSSProperties,
    clearSearchButton: {
      ...flexCenter,
      width: IconSize.md,
      height: IconSize.md,
      borderRadius: Radius.full,
      border: CssValue.none,
      background: 'rgba(255,255,255,0.08)',
      color: Colors.textPrimary,
      cursor: Cursor.pointer,
      flexShrink: 0,
      padding: 0,
      opacity: 1,
      transition: `opacity ${DOCK_ACTION_FADE_MS}ms ease`,
    } as CSSProperties,
    clearSearchButtonHidden: {
      ...flexCenter,
      width: IconSize.md,
      height: IconSize.md,
      borderRadius: Radius.full,
      border: CssValue.none,
      background: 'rgba(255,255,255,0.08)',
      color: Colors.textPrimary,
      cursor: Cursor.pointer,
      flexShrink: 0,
      padding: 0,
      opacity: 0,
      pointerEvents: PointerEvents.none,
      transition: `opacity ${DOCK_ACTION_FADE_MS}ms ease`,
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
      opacity: 0.9,
      transition: [
        `opacity ${QUICK_FADE_MS}ms ease`,
        `background-color ${QUICK_FADE_MS}ms ease`,
        `border-color ${QUICK_FADE_MS}ms ease`,
        `box-shadow ${QUICK_FADE_MS}ms ease`,
      ].join(', '),
      cursor: Cursor.text,
    } as CSSProperties,
    searchInputWrapKeyboard: {
      ...frostedCard,
      ...flexRow,
      gap: Gap.sm,
      width: CssValue.full,
      height: Layout.fabSize,
      padding: padding(0, Gap.section),
      borderRadius: Radius.full,
      boxSizing: BoxSizing.borderBox,
      boxShadow: `${Shadow.tooltip}, 0 0 0 1px ${Colors.purpleHighlightBorder}`,
      opacity: 1,
      backgroundColor: 'rgba(18,24,38,0.96)',
      border: `1px solid ${Colors.purpleHighlightBorder}`,
      transition: [
        `opacity ${QUICK_FADE_MS}ms ease`,
        `background-color ${QUICK_FADE_MS}ms ease`,
        `border-color ${QUICK_FADE_MS}ms ease`,
        `box-shadow ${QUICK_FADE_MS}ms ease`,
      ].join(', '),
      cursor: Cursor.text,
    } as CSSProperties,
    dockSearchInputWrap: {
      ...frostedCard,
      ...flexRow,
      gap: DOCK_LABEL_ICON_GAP,
      width: CssValue.full,
      height: Layout.fabSize,
      padding: padding(0, DOCK_LABEL_HORIZONTAL_PADDING),
      borderRadius: Radius.full,
      boxSizing: BoxSizing.borderBox,
      boxShadow: Shadow.tooltip,
      opacity: 0.9,
      transition: [
        `opacity ${QUICK_FADE_MS}ms ease`,
        `background-color ${QUICK_FADE_MS}ms ease`,
        `border-color ${QUICK_FADE_MS}ms ease`,
        `box-shadow ${QUICK_FADE_MS}ms ease`,
      ].join(', '),
      cursor: Cursor.text,
    } as CSSProperties,
    dockSearchInputWrapKeyboard: {
      ...frostedCard,
      ...flexRow,
      gap: DOCK_LABEL_ICON_GAP,
      width: CssValue.full,
      height: Layout.fabSize,
      padding: padding(0, DOCK_LABEL_HORIZONTAL_PADDING),
      borderRadius: Radius.full,
      boxSizing: BoxSizing.borderBox,
      boxShadow: `${Shadow.tooltip}, 0 0 0 1px ${Colors.purpleHighlightBorder}`,
      opacity: 1,
      backgroundColor: 'rgba(18,24,38,0.96)',
      border: `1px solid ${Colors.purpleHighlightBorder}`,
      transition: [
        `opacity ${QUICK_FADE_MS}ms ease`,
        `background-color ${QUICK_FADE_MS}ms ease`,
        `border-color ${QUICK_FADE_MS}ms ease`,
        `box-shadow ${QUICK_FADE_MS}ms ease`,
      ].join(', '),
      cursor: Cursor.text,
    } as CSSProperties,
  }), []);
}