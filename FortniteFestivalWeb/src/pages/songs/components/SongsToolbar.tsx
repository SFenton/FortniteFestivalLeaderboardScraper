/**
 * Toolbar with search, instrument indicator, sort and filter buttons.
 * Extracted from SongsPage.
 */
import { useState, useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { IoSwapVerticalSharp, IoFunnel } from 'react-icons/io5';
import { Size, FAST_FADE_MS } from '@festival/theme';
import { type ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { InstrumentIcon } from '../../../components/display/InstrumentIcons';
import SearchBar from '../../../components/common/SearchBar';
import { ActionPill } from '../../../components/common/ActionPill';
import s from './SongsToolbar.module.css';

/** Total duration for fade-out + width-collapse before unmount (opacity + delayed width + buffer). */
const INST_LEAVE_MS = FAST_FADE_MS * 2 + FAST_FADE_MS / 10;

interface SongsToolbarProps {
  search: string;
  onSearchChange: (q: string) => void;
  instrument: InstrumentKey | null;
  sortActive?: boolean;
  filtersActive: boolean;
  hasPlayer: boolean;
  filteredCount: number;
  totalCount: number;
  onOpenSort: () => void;
  onOpenFilter: () => void;
}

export function SongsToolbar({
  search,
  onSearchChange,
  instrument,
  sortActive,
  filtersActive,
  hasPlayer,
  filteredCount,
  totalCount,
  onOpenSort,
  onOpenFilter,
}: SongsToolbarProps) {
  const { t } = useTranslation();

  // Track displayed instrument for fade transitions
  const [displayedInst, setDisplayedInst] = useState(instrument);
  const [iconVisible, setIconVisible] = useState(!!instrument);
  const prevInst = useRef(instrument);

  useEffect(() => {
    const prev = prevInst.current;
    prevInst.current = instrument;

    if (!prev && instrument) {
      // No icon → icon: mount then fade in
      setDisplayedInst(instrument);
      requestAnimationFrame(() => requestAnimationFrame(() => setIconVisible(true)));
    } else if (prev && !instrument) {
      // Icon → no icon: fade out, then collapse width, then unmount
      setIconVisible(false);
      const timer = setTimeout(() => setDisplayedInst(null), INST_LEAVE_MS);
      return () => clearTimeout(timer);
    } else if (prev && instrument && prev !== instrument) {
      // Swap: fade out, swap, fade in
      setIconVisible(false);
      const timer = setTimeout(() => {
        setDisplayedInst(instrument);
        requestAnimationFrame(() => requestAnimationFrame(() => setIconVisible(true)));
      }, FAST_FADE_MS);
      return () => clearTimeout(timer);
    }
  }, [instrument]);

  const showIconSlot = !!displayedInst || !!instrument;

  return (
    <>
      <div className={s.toolbar}>
        <SearchBar
          value={search}
          onChange={onSearchChange}
          placeholder={t('songs.searchPlaceholder')}
          className={s.searchWrap}
          inputClassName={s.searchInput}
        />
        {showIconSlot && (
          <div className={iconVisible ? s.instSlot : s.instSlotHidden}>
            {displayedInst && <InstrumentIcon instrument={displayedInst} size={Size.iconInstrument} />}
          </div>
        )}
        <div className={s.sortGroup}>
          <ActionPill icon={<IoSwapVerticalSharp size={Size.iconAction} />} label={t('common.sort')} onClick={onOpenSort} active={sortActive} />
          {hasPlayer && (
            <ActionPill
              icon={<IoFunnel size={Size.iconAction} />}
              label={t('common.filter')}
              onClick={onOpenFilter}
              active={filtersActive}
            />
          )}
        </div>
      </div>
      {filtersActive && filteredCount !== totalCount && (
        <div className={s.count}>{t('songs.count', { filtered: filteredCount, total: totalCount })}</div>
      )}
    </>
  );
}
