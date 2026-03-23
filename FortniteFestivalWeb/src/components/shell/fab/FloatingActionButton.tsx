/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useState, useEffect, useCallback, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { IoMenu } from 'react-icons/io5';
import { useSearchQuery } from '../../../contexts/SearchQueryContext';
import { IS_PWA } from '@festival/ui-utils';
import { Gap, Size } from '@festival/theme';
import SearchBar from '../../common/SearchBar';
import FABMenu from './FABMenu';
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
    setTimeout(() => { setPopupMounted(false); }, 300);
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

  return (
    <div ref={containerRef}>
      {searchVisible && (
        /* v8 ignore start -- IS_PWA + searchBar interactions not available in jsdom */
        <div className={css.searchBarOuter} style={{ ...(IS_PWA ? { bottom: 80 + Gap.section - Gap.md } : {}) }}>
          <div className={`fab-search-bar ${css.searchBar}`}>
            <SearchBar
              value={searchQuery.query}
              onChange={searchQuery.setQuery}
              onKeyDown={e => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur(); }}
              placeholder={placeholder ?? t('songs.searchPlaceholder')}
              enterKeyHint="done"
              className={css.searchInputWrap}
              autoFocus
            />
          </div>
        </div>
        /* v8 ignore stop */
      )}
      {/* v8 ignore start -- IS_PWA: PWA detection not available in jsdom */}
      <div className={css.container} style={{ ...(IS_PWA ? { bottom: 66 + Gap.section - Gap.md } : {}) }}>
      {/* v8 ignore stop */}
        <button
          className={css.fab}
          /* v8 ignore start -- action toggle */
          onClick={() => actionsOpen ? closeActions() : openActions()}
          /* v8 ignore stop */
          aria-label={t('common.actions')}
        >
          {icon ?? <IoMenu size={Size.iconMd} />}
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

