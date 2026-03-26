/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useState, useEffect, useCallback, useRef, useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { IoMenu } from 'react-icons/io5';
import { useSearchQuery } from '../../../contexts/SearchQueryContext';
import { IS_PWA } from '@festival/ui-utils';
import { Colors, Gap, Radius, Layout, MaxWidth, Shadow, ZIndex, Display, Align, Position, Cursor, BoxSizing, IconSize, PointerEvents, Overflow, CssValue, FAB_DISMISS_MS, frostedCard, purpleGlass, flexColumn, flexCenter, flexRow, padding, border, Border } from '@festival/theme';
import SearchBar from '../../common/SearchBar';
import FABMenu from './FABMenu';

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
  mode: _mode,
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
  /* v8 ignore stop */

  const searchQuery = useSearchQuery();

  const containerRef = useRef<HTMLDivElement>(null);

  /* v8 ignore start — click-outside handler */
  useEffect(() => {
    if (!actionsOpen) return;
    const handler = (e: MouseEvent) => {
      if (containerRef.current && !containerRef.current.contains(e.target as Node)) {
        e.stopPropagation();
        closeActions();
      }
    };
    document.addEventListener('click', handler, true);
    return () => document.removeEventListener('click', handler, true);
  }, [actionsOpen, closeActions]);
  /* v8 ignore stop */

  const s = useFABStyles();

  return (
    <div ref={containerRef}>
      {searchVisible && (
        /* v8 ignore start -- IS_PWA + searchBar interactions not available in jsdom */
        <div style={{ ...s.searchBarOuter, ...(IS_PWA ? { bottom: Layout.fabBottom + Gap.section - Gap.md } : {}) }}>
          <div className="fab-search-bar" style={s.searchBar}>
            <SearchBar
              value={searchQuery.query}
              onChange={searchQuery.setQuery}
              onKeyDown={e => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur(); }}
              placeholder={placeholder ?? t('songs.searchPlaceholder')}
              enterKeyHint="done"
              style={s.searchInputWrap}
              autoFocus
            />
          </div>
        </div>
        /* v8 ignore stop */
      )}
      {/* v8 ignore start -- IS_PWA: PWA detection not available in jsdom */}
      <div style={{ ...s.container, ...(IS_PWA ? { bottom: Layout.pwaBottomOffset + Gap.section - Gap.md } : {}) }}>
      {/* v8 ignore stop */}
        <button
          style={s.fab}
          /* v8 ignore start -- action toggle */
          onClick={() => actionsOpen ? closeActions() : openActions()}
          /* v8 ignore stop */
          aria-label={t('common.actions')}
        >
          {icon ?? <IoMenu size={IconSize.md} />}
        </button>
        {popupMounted && (
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
      bottom: Layout.fabBottom,
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
      bottom: Layout.fabBottom,
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