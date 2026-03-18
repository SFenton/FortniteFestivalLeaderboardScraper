import { useEffect, useState, useCallback, useRef, useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate, useLocation } from 'react-router-dom';
import {
  computeOverallStats,
  groupByInstrument,
} from '../../../player/helpers/playerStats';
import { SERVER_INSTRUMENT_KEYS as INSTRUMENT_KEYS, type ServerInstrumentKey as InstrumentKey, type PlayerResponse, type ServerSong as Song } from '@festival/core/api/serverTypes';
import { Gap, Layout, Radius, frostedCard, STAGGER_ENTRY_OFFSET, QUERY_NARROW_GRID } from '@festival/theme';
import s from '../../../../components/player/PlayerPage.module.css';
import { SelectProfilePill } from '../../../../components/player/SelectProfilePill';
import SyncBanner from '../../../../components/page/SyncBanner';
import { useSettings, isInstrumentVisible } from '../../../../contexts/SettingsContext';
import { loadSongSettings, saveSongSettings } from '../../../../utils/songSettings';
import Page from '../../../Page';
import { useIsMobile } from '../../../../hooks/ui/useIsMobile';
import { useMediaQuery } from '../../../../hooks/ui/useMediaQuery';
import { IS_IOS, IS_ANDROID, IS_PWA } from '@festival/ui-utils';
import { useTrackedPlayer } from '../../../../hooks/data/useTrackedPlayer';
import { useScoreFilter } from '../../../../hooks/data/useScoreFilter';
import { usePlayerPageSelect } from '../../../../contexts/FabSearchContext';
import ConfirmAlert from '../../../../components/modals/ConfirmAlert';
import FadeIn from '../../../../components/page/FadeIn';
import PlayerSectionHeading from '../../../player/sections/PlayerSectionHeading';
import { buildOverallSummaryItems } from '../../../player/sections/OverallSummarySection';
import { buildInstrumentStatsItems } from '../../../player/sections/InstrumentStatsSection';
import { buildTopSongsItems } from '../../../player/components/TopSongsSection';
import type { PlayerItem } from '../../../player/helpers/playerPageTypes';
import type { SyncPhase } from '../../../../hooks/data/useSyncStatus';

export interface PlayerContentProps {
  data: PlayerResponse;
  songs: Song[];
  isSyncing: boolean;
  phase: SyncPhase;
  backfillProgress: number;
  historyProgress: number;
  isTrackedPlayer: boolean;
  skipAnim: boolean;
}

export default function PlayerContent({
  data,
  songs,
  isSyncing,
  phase: syncPhase,
  backfillProgress,
  historyProgress,
  isTrackedPlayer,
  skipAnim,
}: PlayerContentProps) {
  const { t } = useTranslation();
  const { settings } = useSettings();
  const location = useLocation();
  const navigate = useNavigate();
  const scrollRef = useRef<HTMLDivElement>(null);
  const { player: trackedPlayer, setPlayer } = useTrackedPlayer();
  const [pendingSwitch, setPendingSwitch] = useState<(() => void) | null>(null);
  const { filterPlayerScores } = useScoreFilter();
  const { registerPlayerPageSelect } = usePlayerPageSelect();

  // Register FAB "Select as Profile" action
  useEffect(() => {
    if (trackedPlayer?.accountId === data.accountId) {
      /* v8 ignore start */
      registerPlayerPageSelect(null);
      return;
      /* v8 ignore stop */
    }
    registerPlayerPageSelect({
      displayName: data.displayName,
      /* v8 ignore start — profile switch callbacks */
      onSelect: () => {
        if (trackedPlayer && trackedPlayer.accountId !== data.accountId) {
          setPendingSwitch(() => () => setPlayer({ accountId: data.accountId, displayName: data.displayName }));
        } else {
          setPlayer({ accountId: data.accountId, displayName: data.displayName });
        /* v8 ignore stop */
        }
      },
    });
    return () => registerPlayerPageSelect(null);
  }, [data.accountId, data.displayName, trackedPlayer, setPlayer, registerPlayerPageSelect]);

  // Helper: wrap a navigation action with profile-switch logic when viewing another player
  /* v8 ignore start — navigation + profile switch */
  const withProfileSwitch = useCallback((action: () => void) => {
    if (!isTrackedPlayer) {
      const selectAndGo = () => {
        setPlayer({ accountId: data.accountId, displayName: data.displayName });
        action();
      };
      if (trackedPlayer && trackedPlayer.accountId !== data.accountId) {
        setPendingSwitch(() => selectAndGo);
      } else { selectAndGo(); }
    } else { action(); }
    /* v8 ignore stop */
  }, [isTrackedPlayer, trackedPlayer, data.accountId, data.displayName, setPlayer]);

  const effectiveScores = useMemo(() => {
    const visible = data.scores.filter(s => isInstrumentVisible(settings, s.instrument as InstrumentKey));
    return filterPlayerScores(visible);
  }, [data.scores, settings, filterPlayerScores],
  );
  const visibleKeys = useMemo(() =>
    INSTRUMENT_KEYS.filter(k => isInstrumentVisible(settings, k)),
    [settings],
  );

  const songMap = useMemo(() => new Map(songs.map((s) => [s.songId, s])), [songs]);
  const byInstrument = useMemo(() => groupByInstrument(effectiveScores), [effectiveScores]);
  const overallStats = useMemo(() => computeOverallStats(effectiveScores), [effectiveScores]);

  // Stable navigation helpers to reduce closure overhead in onClick handlers
  /* v8 ignore start — navigation helpers */
  const navigateToSongs = useCallback((settingsUpdater: (s: ReturnType<typeof loadSongSettings>) => ReturnType<typeof loadSongSettings>) => {
    withProfileSwitch(() => {
      const s = loadSongSettings();
      saveSongSettings(settingsUpdater(s));
      navigate('/songs', { state: { backTo: location.pathname, restagger: true } });
    /* v8 ignore stop */
    });
  }, [withProfileSwitch, navigate, location.pathname]);

  /* v8 ignore start — navigation helper */
  const navigateToSongDetail = useCallback((songId: string, instrument: InstrumentKey, opts?: { autoScroll?: boolean }) => {
    withProfileSwitch(() => navigate(`/songs/${songId}?instrument=${encodeURIComponent(instrument)}`, { state: { backTo: location.pathname, ...opts } }));
    /* v8 ignore stop */
  }, [withProfileSwitch, navigate, location.pathname]);

  // Build a completely flat list of small items — each becomes a direct child
  // of the grid so each gets a staggered fade-in animation.
  const items: PlayerItem[] = [];

  const cardStyle: CSSProperties = {
    ...frostedCard,
    borderRadius: Radius.md,
  };

  // --- Sync banner ---
  if (isSyncing) {
    items.push({
      key: 'sync',
      span: true,
      heightEstimate: 150,
      node: (
        <SyncBanner
          displayName={data.displayName}
          phase={syncPhase}
          backfillProgress={backfillProgress}
          historyProgress={historyProgress}
        />
      ),
    });
  }

  // --- Overall summary stat boxes ---
  items.push(...buildOverallSummaryItems(t, overallStats, songs.length, visibleKeys, navigateToSongs, navigateToSongDetail, cardStyle));

  // --- Instrument Statistics heading ---
  items.push({
    key: 'inst-heading',
    span: true,
    heightEstimate: 80,
    node: (
      <PlayerSectionHeading title={t('player.instrumentStats')} description={t('player.instrumentStatsDesc', { name: data.displayName })} />
    ),
  });

  // --- Per-instrument: header + stat boxes + percentile rows ---
  for (const inst of visibleKeys) {
    const scores = byInstrument.get(inst);
    if (!scores || scores.length === 0) continue;
    items.push(...buildInstrumentStatsItems(t, inst, scores, songs.length, data.displayName, navigateToSongs, navigateToSongDetail, cardStyle));
  }

  // --- Top Songs heading ---
  items.push({
    key: 'top-heading',
    span: true,
    heightEstimate: 80,
    node: (
      <PlayerSectionHeading title={t('player.topSongsPerInstrument')} description={t('player.topSongsPerInstrumentDesc', {name: data.displayName})} />
    ),
  });

  // --- Top/Bottom song rows ---
  for (const inst of visibleKeys) {
    const scores = byInstrument.get(inst);
    if (!scores || scores.length === 0) continue;
    items.push(...buildTopSongsItems(t, inst, scores, songMap, data.displayName, navigateToSongDetail));
  }

  // Wire up container-level scroll fade
  const fadeDeps = useMemo(() => [items.length], [items.length]);
  const hasFab = useIsMobile();

  const isNarrowGrid = useMediaQuery(QUERY_NARROW_GRID);

  // Only render the button container on desktop non-PWA; animate visibility
  const canShowSelectBtn = !hasFab && !IS_IOS && !IS_ANDROID && !IS_PWA;
  const selectBtnVisible = canShowSelectBtn && !isTrackedPlayer && trackedPlayer?.accountId !== data.accountId;

  return (
    <Page
      scrollRef={scrollRef}
      scrollDeps={fadeDeps}
      scrollClassName={s.scrollArea}
      containerClassName={s.container}
      before={
        <div className={s.playerNameBar}>
          <h1 className={s.playerName}>{data.displayName}</h1>
          {canShowSelectBtn && (
            <SelectProfilePill
              visible={selectBtnVisible}
              onClick={() => {
                /* v8 ignore start */
                if (trackedPlayer && trackedPlayer.accountId !== data.accountId) {
                  setPendingSwitch(() => () => setPlayer({ accountId: data.accountId, displayName: data.displayName }));
                } else {
                  setPlayer({ accountId: data.accountId, displayName: data.displayName });
                /* v8 ignore stop */
                }
              }}
            />
          )}
        </div>
      }
      after={pendingSwitch ? (
        <ConfirmAlert
          title={t('player.switchTo', {name: data.displayName})}
          message={t('player.switchConfirmMessage', {name: data.displayName})}
          /* v8 ignore start */
          onNo={() => setPendingSwitch(null)}
          onYes={() => { setPendingSwitch(null); pendingSwitch(); }}
          /* v8 ignore stop */
        />
      ) : undefined}
    >
        <div style={{ ...(hasFab ? { paddingBottom: Layout.fabPaddingBottom } : {}) }}>
          <div className={s.gridList} style={{ ...(isNarrowGrid ? { gridTemplateColumns: 'minmax(0, 1fr)' } : {}) }}>
            {(() => {
              // Compute which items are in the initial viewport by accumulating
              // estimated row heights.  The grid is 2-col: span items take a full
              // row, non-span items pair up (each row = max of the pair's height).
              /* v8 ignore start -- SSR guard: window always defined in jsdom */
              const vh = typeof window !== 'undefined' ? window.innerHeight : 900;
              /* v8 ignore stop */
              const gap = Gap.md;
              let accHeight = 0;
              let col = 0; // 0 = left, 1 = right in the 2-col grid
              let rowMax = 0;
              let visibleCount = items.length; // default: animate all

              for (let i = 0; i < items.length; i++) {
                const item = items[i]!;
                if (item.span) {
                  // Flush any pending half-row
                  if (col === 1) {
                    accHeight += rowMax + gap;
                    col = 0;
                    rowMax = 0;
                  }
                  accHeight += item.heightEstimate + gap;
                } else {
                  rowMax = Math.max(rowMax, item.heightEstimate);
                  col++;
                  if (col === 2) {
                    accHeight += rowMax + gap;
                    col = 0;
                    rowMax = 0;
                  }
                }
                if (accHeight > vh && visibleCount === items.length) {
                  visibleCount = i + 2; // +1 for the partially-visible item, +1 for 0-index
                }
              }

              const lastVisibleDelay = visibleCount * STAGGER_ENTRY_OFFSET;
              return items.map((item, i) => {
                const delay = skipAnim ? undefined : (i < visibleCount ? (i + 1) * STAGGER_ENTRY_OFFSET : lastVisibleDelay);
                return (
                  <FadeIn key={item.key} delay={delay} className={item.span ? s.gridFullWidth : undefined} style={item.style}>
                    {item.node}
                  </FadeIn>
                );
              });
            })()}
          </div>
        </div>
    </Page>
  );
}
