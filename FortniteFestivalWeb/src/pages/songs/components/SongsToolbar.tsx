/**
 * Toolbar with search, instrument indicator, sort and filter buttons.
 * Extracted from SongsPage.
 */
import { useTranslation } from 'react-i18next';
import { IoSwapVerticalSharp, IoFunnel } from 'react-icons/io5';
import { Size } from '@festival/theme';
import { type ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { InstrumentIcon } from '../../../components/display/InstrumentIcons';
import SearchBar from '../../../components/common/SearchBar';
import { ActionPill } from '../../../components/common/ActionPill';
import s from './SongsToolbar.module.css';

interface SongsToolbarProps {
  search: string;
  onSearchChange: (q: string) => void;
  instrument: InstrumentKey | null;
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
  filtersActive,
  hasPlayer,
  filteredCount,
  totalCount,
  onOpenSort,
  onOpenFilter,
}: SongsToolbarProps) {
  const { t } = useTranslation();

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
        {instrument && (
          <InstrumentIcon instrument={instrument} size={Size.iconInstrumentXs} />
        )}
        <div className={s.sortGroup}>
          <ActionPill icon={<IoSwapVerticalSharp size={Size.iconAction} />} label={t('common.sort')} onClick={onOpenSort} />
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
        <div className={s.count}>{filteredCount} of {totalCount} songs</div>
      )}
    </>
  );
}
