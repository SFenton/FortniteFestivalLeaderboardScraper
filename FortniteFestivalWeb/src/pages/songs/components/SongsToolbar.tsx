/**
 * Toolbar with search, instrument indicator, sort and filter buttons.
 * Extracted from SongsPage.
 */
import { useState, useEffect, useRef, useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { IoSwapVerticalSharp, IoFunnel } from 'react-icons/io5';
import { Colors, Font, Gap, Radius, Size, FAST_FADE_MS, frostedCard, flexRow, flexCenter, Display, Align, Justify, Cursor, BoxSizing, Overflow, CssValue, transition, transitions } from '@festival/theme';
import { type ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { InstrumentIcon } from '../../../components/display/InstrumentIcons';
import SearchBar from '../../../components/common/SearchBar';
import { ActionPill } from '../../../components/common/ActionPill';

const FAST = FAST_FADE_MS;

/** Total duration for fade-out + width-collapse before unmount (opacity + delayed width + buffer). */
const INST_LEAVE_MS = FAST_FADE_MS * 2 + FAST_FADE_MS / 10;

interface SongsToolbarProps {
  search: string;
  onSearchChange: (q: string) => void;
  instrument: InstrumentKey | null;
  sortActive?: boolean;
  filtersActive: boolean;
  hasSongs: boolean;
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
  hasSongs,
  hasPlayer,
  filteredCount,
  totalCount,
  onOpenSort,
  onOpenFilter,
}: SongsToolbarProps) {
  const { t } = useTranslation();
  const styles = useStyles();

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

  // Track sort button for fade transitions (gated on songs loaded)
  const [sortVisible, setSortVisible] = useState(hasSongs);
  const prevHasSongs = useRef(hasSongs);

  useEffect(() => {
    const prev = prevHasSongs.current;
    prevHasSongs.current = hasSongs;

    if (!prev && hasSongs) {
      requestAnimationFrame(() => requestAnimationFrame(() => setSortVisible(true)));
    } else if (prev && !hasSongs) {
      setSortVisible(false);
    }
  }, [hasSongs]);

  // Track filter button for fade transitions (gated on player data loaded)
  const [filterVisible, setFilterVisible] = useState(hasPlayer);
  const prevHasPlayer = useRef(hasPlayer);

  useEffect(() => {
    const prev = prevHasPlayer.current;
    prevHasPlayer.current = hasPlayer;

    if (!prev && hasPlayer) {
      requestAnimationFrame(() => requestAnimationFrame(() => setFilterVisible(true)));
    } else if (prev && !hasPlayer) {
      setFilterVisible(false);
    }
  }, [hasPlayer]);

  return (
    <>
      <div style={styles.toolbar}>
        <SearchBar
          value={search}
          onChange={onSearchChange}
          placeholder={t('songs.searchPlaceholder')}
          style={styles.searchWrap}
        />
        {showIconSlot && (
          <div style={iconVisible ? styles.instSlot : styles.instSlotHidden}>
            {displayedInst && <InstrumentIcon instrument={displayedInst} size={Size.iconInstrument} />}
          </div>
        )}
        <div style={styles.sortGroup}>
          <div style={sortVisible ? styles.sortSlot : styles.sortSlotHidden}>
            <ActionPill icon={<IoSwapVerticalSharp size={Size.iconAction} />} label={t('common.sort')} onClick={onOpenSort} active={sortActive} />
          </div>
          <div style={filterVisible ? styles.filterSlot : styles.filterSlotHidden}>
            <ActionPill
              icon={<IoFunnel size={Size.iconAction} />}
              label={t('common.filter')}
              onClick={onOpenFilter}
              active={filtersActive}
            />
          </div>
        </div>
      </div>
      {filtersActive && filteredCount !== totalCount && (
        <div style={styles.count}>{t('songs.count', { filtered: filteredCount, total: totalCount })}</div>
      )}
    </>
  );
}

function useStyles() {
  return useMemo(() => ({
    toolbar: { ...flexRow, flexWrap: 'wrap', gap: 0, marginBottom: Gap.md } as CSSProperties,
    searchWrap: { ...frostedCard, flex: 1, minWidth: 200, height: Size.iconXl, ...flexRow, gap: Gap.sm, padding: `0 ${Gap.xl}px`, boxSizing: BoxSizing.borderBox, borderRadius: Radius.full, cursor: Cursor.text, marginRight: Gap.xl } as CSSProperties,
    searchInput: { flex: 1, background: CssValue.none, border: CssValue.none, outline: CssValue.none, color: Colors.textPrimary, fontSize: Font.md } as CSSProperties,
    sortGroup: { display: Display.flex } as CSSProperties,
    instSlot: { width: Size.iconXl, marginRight: Gap.xl, opacity: 1, overflow: Overflow.hidden, flexShrink: 0, transition: transitions(transition('opacity', FAST), transition('width', FAST), transition('margin-right', FAST)), ...flexCenter } as CSSProperties,
    instSlotHidden: { width: 0, marginRight: 0, opacity: 0, overflow: Overflow.hidden, flexShrink: 0, transition: transitions(transition('opacity', FAST), `width ${FAST}ms ease ${FAST}ms`, `margin-right ${FAST}ms ease ${FAST}ms`), display: Display.flex, alignItems: Align.center, justifyContent: Justify.center } as CSSProperties,
    sortSlot: { opacity: 1, maxWidth: Size.iconXl * 3, transition: transitions(transition('opacity', FAST), transition('max-width', FAST)) } as CSSProperties,
    sortSlotHidden: { opacity: 0, maxWidth: 0, transition: transitions(transition('opacity', FAST), `max-width ${FAST}ms ease ${FAST}ms`) } as CSSProperties,
    filterSlot: { opacity: 1, maxWidth: Size.iconXl * 3, marginLeft: Gap.sm, transition: transitions(transition('opacity', FAST), transition('max-width', FAST), transition('margin-left', FAST)) } as CSSProperties,
    filterSlotHidden: { opacity: 0, maxWidth: 0, marginLeft: 0, transition: transitions(transition('opacity', FAST), `max-width ${FAST}ms ease ${FAST}ms`, `margin-left ${FAST}ms ease ${FAST}ms`) } as CSSProperties,
    count: { fontSize: Font.sm, color: Colors.textTertiary, marginBottom: Gap.md } as CSSProperties,
  }), []);
}
