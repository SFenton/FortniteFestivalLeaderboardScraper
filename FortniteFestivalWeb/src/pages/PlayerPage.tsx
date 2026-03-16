import { useEffect, useState, useCallback, useRef, useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams, Link, useNavigate, useNavigationType, useLocation } from 'react-router-dom';
import { IoPerson } from 'react-icons/io5';
import { formatPercentileBucket } from '@festival/core';
import {
  type InstrumentStats,
  computeInstrumentStats,
  computeOverallStats,
  groupByInstrument,
  formatClamped,
  formatClamped2,
  accuracyColor,
} from '../components/player/playerStats';
import { useFestival } from '../contexts/FestivalContext';
import { usePlayerData } from '../contexts/PlayerDataContext';
import { useSyncStatus, type SyncPhase } from '../hooks/useSyncStatus';
import { api } from '../api/client';
import {
  INSTRUMENT_KEYS,
  INSTRUMENT_LABELS,
  type InstrumentKey,
  type PlayerResponse,
  type PlayerScore,
  type Song,
} from '../models';
import { Colors, Font, Gap, Radius, Layout, MaxWidth, Size, goldFill, goldOutline, goldOutlineSkew, frostedCard } from '@festival/theme';
import s from './PlayerPage.module.css';
import { InstrumentIcon } from '../components/InstrumentIcons';
import SyncBanner from '../components/player/SyncBanner';
import { useSettings, isInstrumentVisible } from '../contexts/SettingsContext';
import { loadSongSettings, saveSongSettings, defaultSongFilters } from '../components/songSettings';
import { useScrollMask } from '../hooks/useScrollMask';
import { useIsMobile } from '../hooks/useIsMobile';
import { IS_IOS, IS_ANDROID, IS_PWA } from '../utils/platform';
import { useTrackedPlayer } from '../hooks/useTrackedPlayer';
import { useScoreFilter } from '../hooks/useScoreFilter';
import StatBox from '../components/player/StatBox';
import { useFabSearch } from '../contexts/FabSearchContext';
import AlbumArt from '../components/AlbumArt';
import ConfirmAlert from '../components/modals/ConfirmAlert';
import FadeInDiv from '../components/FadeInDiv';
import { useStaggerRush } from '../hooks/useStaggerRush';

/** Track rendered accounts so we can skip stagger animation on revisit.
 *  Stores the last-visited player page account and the tracked (statistics) account. */
let _renderedPlayerAccount: string | null = null;
let _renderedTrackedAccount: string | null = null;
let _cachedPlayerData: { accountId: string; data: PlayerResponse } | null = null;

export function clearPlayerPageCache() {
  _renderedPlayerAccount = null;
  _renderedTrackedAccount = null;
  _cachedPlayerData = null;
}

export default function PlayerPage({ accountId: propAccountId }: { accountId?: string } = {}) {
  const { t } = useTranslation();
  const params = useParams<{ accountId: string }>();
  const accountId = propAccountId ?? params.accountId;
  const navType = useNavigationType();
  const {
    state: { songs },
  } = useFestival();

  // Use cached context data when viewing the tracked player (statistics tab)
  const ctx = usePlayerData();
  const isTrackedPlayer = !!propAccountId;

  // Local state for when viewing an arbitrary player via URL
  const cachedData = _cachedPlayerData?.accountId === accountId ? _cachedPlayerData.data : null;
  const [localData, setLocalData] = useState<PlayerResponse | null>(cachedData);
  const [localLoading, setLocalLoading] = useState(!isTrackedPlayer && !cachedData);
  const [localError, setLocalError] = useState<string | null>(null);
  const hasDataRef = useRef(!!cachedData);

  const { isSyncing: localSyncing, phase: localPhase, backfillProgress: localBfProg, historyProgress: localHrProg, justCompleted, clearCompleted } =
    useSyncStatus(!isTrackedPlayer ? accountId : undefined);

  const fetchPlayer = useCallback(async () => {
    if (!accountId || isTrackedPlayer) return;
    if (!hasDataRef.current) setLocalLoading(true);
    setLocalError(null);
    try {
      const res = await api.getPlayer(accountId);
      setLocalData(res);
      _cachedPlayerData = { accountId, data: res };
      hasDataRef.current = true;
    } catch (e) {
      setLocalError(e instanceof Error ? e.message : t('player.failedToLoad'));
    } finally {
      setLocalLoading(false);
    }
  }, [accountId, isTrackedPlayer]);

  useEffect(() => {
    if (isTrackedPlayer) return;
    void fetchPlayer();
  }, [fetchPlayer, isTrackedPlayer]);

  useEffect(() => {
    if (justCompleted) {
      clearCompleted();
      void fetchPlayer();
    }
  }, [justCompleted, clearCompleted, fetchPlayer]);

  // Resolve effective values: context for tracked player, local for others
  const data = isTrackedPlayer ? ctx.playerData : localData;
  const loading = isTrackedPlayer ? ctx.playerLoading : localLoading;
  const error = isTrackedPlayer ? ctx.playerError : localError;
  const isSyncing = isTrackedPlayer ? ctx.isSyncing : localSyncing;
  const phase = isTrackedPlayer ? ctx.syncPhase : localPhase;
  const backfillProgress = isTrackedPlayer ? ctx.backfillProgress : localBfProg;
  const historyProgress = isTrackedPlayer ? ctx.historyProgress : localHrProg;

  // Skip stagger if we've rendered this account before.
  const hasRendered = isTrackedPlayer
    ? _renderedTrackedAccount === accountId
    : _renderedPlayerAccount === accountId;
  const prevAccountRef = useRef(accountId);
  const skipAnimRef = useRef(hasRendered);
  // When accountId changes within the same component instance, re-evaluate skip
  if (prevAccountRef.current !== accountId) {
    prevAccountRef.current = accountId;
    const alreadyRendered = isTrackedPlayer
      ? _renderedTrackedAccount === accountId
      : _renderedPlayerAccount === accountId;
    skipAnimRef.current = alreadyRendered;
  }
  const skipAnim = skipAnimRef.current;
  if (data) {
    if (isTrackedPlayer) _renderedTrackedAccount = accountId!;
    else _renderedPlayerAccount = accountId!;
  }

  if (loading) return <div className={s.page}><div className={s.center}><div className={s.arcSpinner} /></div></div>;
  if (error) return <div className={s.page}><div className={s.centerError}>{error}</div></div>;
  if (!data) return <div className={s.page}><div className={s.center}>{t('player.playerNotFound')}</div></div>;

  return <PlayerContent key={accountId} data={data} songs={songs} isSyncing={isSyncing} phase={phase} backfillProgress={backfillProgress} historyProgress={historyProgress} isTrackedPlayer={isTrackedPlayer} skipAnim={skipAnim} />;
}

function PlayerContent({
  data,
  songs,
  isSyncing,
  phase: syncPhase,
  backfillProgress,
  historyProgress,
  isTrackedPlayer,
  skipAnim,
}: {
  data: PlayerResponse;
  songs: Song[];
  isSyncing: boolean;
  phase: SyncPhase;
  backfillProgress: number;
  historyProgress: number;
  isTrackedPlayer: boolean;
  skipAnim: boolean;
}) {
  const { t } = useTranslation();
  const { settings } = useSettings();
  const location = useLocation();
  const navigate = useNavigate();
  const scrollRef = useRef<HTMLDivElement>(null);
  const { player: trackedPlayer, setPlayer } = useTrackedPlayer();
  const [pendingSwitch, setPendingSwitch] = useState<(() => void) | null>(null);
  const { filterPlayerScores } = useScoreFilter();
  const { registerPlayerPageSelect } = useFabSearch();

  // Register FAB "Select as Profile" action
  useEffect(() => {
    if (trackedPlayer?.accountId === data.accountId) {
      registerPlayerPageSelect(null);
      return;
    }
    registerPlayerPageSelect({
      displayName: data.displayName,
      onSelect: () => {
        if (trackedPlayer && trackedPlayer.accountId !== data.accountId) {
          setPendingSwitch(() => () => setPlayer({ accountId: data.accountId, displayName: data.displayName }));
        } else {
          setPlayer({ accountId: data.accountId, displayName: data.displayName });
        }
      },
    });
    return () => registerPlayerPageSelect(null);
  }, [data.accountId, data.displayName, trackedPlayer, setPlayer, registerPlayerPageSelect]);

  // Helper: wrap a navigation action with profile-switch logic when viewing another player
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

  // Build a completely flat list of small items — each becomes a direct child
  // of the grid so each gets a staggered fade-in animation.
  type Item = { key: string; node: React.ReactNode; span: boolean; style?: CSSProperties; heightEstimate: number };
  const items: Item[] = [];

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

  // --- Overall summary stat boxes (each is its own item) ---
  const overallAccColor = overallStats.avgAccuracy > 0
    ? (overallStats.avgAccuracy / 10000 >= 100 && overallStats.fcPercent === '100.0'
        ? Colors.gold
        : accuracyColor(overallStats.avgAccuracy / 10000))
    : undefined;
  const overallSongsAllPlayed = overallStats.songsPlayed >= songs.length && songs.length > 0;
  const overallFcIs100 = overallStats.fcPercent === '100.0';
  const overallFcValue = overallFcIs100
    ? overallStats.fcCount.toLocaleString()
    : `${overallStats.fcCount} (${formatClamped(parseFloat(overallStats.fcPercent))}%)`;

  const summaryBoxes: { label: string; value: React.ReactNode; color?: string; onClick?: () => void }[] = [
    { label: t('player.songsPlayed'), value: overallStats.songsPlayed.toLocaleString(), color: overallSongsAllPlayed ? Colors.statusGreen : undefined, onClick: () => {
      withProfileSwitch(() => {
        const s = loadSongSettings();
        const hasScores: Record<string, boolean> = {};
        for (const k of visibleKeys) hasScores[k] = true;
        saveSongSettings({ ...s, instrument: null, sortMode: 'title', sortAscending: true, filters: { ...defaultSongFilters(), hasScores } });
        navigate('/songs', { state: { backTo: location.pathname, restagger: true } });
      });
    } },
    { label: t('player.fullCombos'), value: overallFcValue, color: overallFcIs100 ? Colors.gold : undefined, onClick: () => {
      withProfileSwitch(() => {
        const s = loadSongSettings();
        const hasFCs: Record<string, boolean> = {};
        for (const k of visibleKeys) hasFCs[k] = true;
        saveSongSettings({ ...s, instrument: null, sortMode: 'title', sortAscending: true, filters: { ...defaultSongFilters(), hasFCs } });
        navigate('/songs', { state: { backTo: location.pathname, restagger: true } });
      });
    } },
    { label: t('player.goldStars'), value: overallStats.goldStarCount.toLocaleString(), color: Colors.gold },
    { label: t('player.avgAccuracy'), value: overallStats.avgAccuracy > 0 ? formatClamped(overallStats.avgAccuracy / 10000) + '%' : '—', color: overallAccColor },
    { label: t('player.bestRank'), value: overallStats.bestRank > 0 ? `#${overallStats.bestRank.toLocaleString()}` : '—', onClick: overallStats.bestRankSongId ? () => withProfileSwitch(() => navigate(`/songs/${overallStats.bestRankSongId}?instrument=${encodeURIComponent(overallStats.bestRankInstrument!)}`, { state: { backTo: location.pathname, autoScroll: true } })) : undefined },
  ];
  for (const box of summaryBoxes) {
    items.push({ key: `sum-${box.label}`, span: false, heightEstimate: 100, style: cardStyle, node: <StatBox label={box.label} value={box.value} color={box.color} onClick={box.onClick} /> });
  }

  // --- Instrument Statistics heading ---
  items.push({
    key: 'inst-heading',
    span: true,
    heightEstimate: 80,
    node: (
      <div style={{ marginTop: Gap.section }}>
        <h2 className={s.sectionTitle}>{t('player.instrumentStats')}</h2>
        <p className={s.sectionDesc}>A quick look at {data.displayName}'s overall Festival statistics per instrument.</p>
      </div>
    ),
  });

  // --- Per-instrument: header + stat boxes + percentile rows ---
  for (const inst of visibleKeys) {
    const scores = byInstrument.get(inst);
    if (!scores || scores.length === 0) continue;
    const stats = computeInstrumentStats(scores, songs.length);

    // Instrument header
    items.push({
      key: `inst-hdr-${inst}`,
      span: true,
      heightEstimate: 64,
      node: (
        <div className={s.instCardHeader}>
          <InstrumentIcon instrument={inst} size={48} />
          <span className={s.instCardTitle}>{INSTRUMENT_LABELS[inst]}</span>
        </div>
      ),
    });

    // Stat boxes (each is its own grid item)
    const cards: { label: string; value: React.ReactNode; color?: string; to?: string; onClick?: () => void }[] = [];
    if (stats.songsPlayed > 0) cards.push({ label: t('player.songsPlayed'), value: stats.songsPlayed.toLocaleString(), color: stats.songsPlayed >= songs.length ? Colors.statusGreen : undefined, onClick: () => {
      withProfileSwitch(() => {
        const s = loadSongSettings();
        saveSongSettings({ ...s, instrument: inst, sortMode: 'score', sortAscending: true, filters: { ...cleanFilters(s), hasScores: { ...s.filters.hasScores, [inst]: true } } });
        navigate('/songs', { state: { backTo: location.pathname, restagger: true } });
      });
    } });
    if (stats.fcCount > 0) cards.push({ label: t('player.fcs'), value: stats.fcPercent === '100.0' ? stats.fcCount.toLocaleString() : `${stats.fcCount} (${stats.fcPercent}%)`, color: stats.fcPercent === '100.0' ? Colors.gold : undefined, onClick: () => {
      withProfileSwitch(() => {
        const s = loadSongSettings();
        saveSongSettings({ ...s, instrument: inst, sortMode: 'score', sortAscending: true, filters: { ...cleanFilters(s), hasFCs: { ...s.filters.hasFCs, [inst]: true } } });
        navigate('/songs', { state: { backTo: location.pathname, restagger: true } });
      });
    } });

    // Star count cards — clickable to filter songs by star level
    const STAR_CARDS: { count: number; label: string; starKey: number; color?: string }[] = [
      { count: stats.goldStarCount, label: t('player.goldStars'), starKey: 6, color: Colors.gold },
      { count: stats.fiveStarCount, label: t('player.fiveStars'), starKey: 5 },
      { count: stats.fourStarCount, label: t('player.fourStars'), starKey: 4 },
      { count: stats.threeStarCount, label: t('player.threeStars'), starKey: 3 },
      { count: stats.twoStarCount, label: t('player.twoStars'), starKey: 2 },
      { count: stats.oneStarCount, label: t('player.oneStar'), starKey: 1 },
    ];
    // Build clean filters: preserve other instruments' missing/has, clear current instrument's, reset instrument-specific filters
    const cleanFilters = (s: ReturnType<typeof loadSongSettings>) => ({
      ...s.filters,
      seasonFilter: {},
      percentileFilter: {},
      starsFilter: {},
      difficultyFilter: {},
      missingScores: { ...s.filters.missingScores, [inst]: false },
      missingFCs: { ...s.filters.missingFCs, [inst]: false },
      hasScores: { ...s.filters.hasScores, [inst]: false },
      hasFCs: { ...s.filters.hasFCs, [inst]: false },
    });

    const makeStarNav = (starKey: number) => () => {
      withProfileSwitch(() => {
        const s = loadSongSettings();
        const starsFilter: Record<number, boolean> = { 0: false, 1: false, 2: false, 3: false, 4: false, 5: false, 6: false, [starKey]: true };
        saveSongSettings({ ...s, instrument: inst, sortMode: 'stars', sortAscending: true, filters: { ...cleanFilters(s), starsFilter } });
        navigate('/songs', { state: { backTo: location.pathname, restagger: true } });
      });
    };
    for (const sc of STAR_CARDS) {
      if (sc.count > 0) cards.push({ label: sc.label, value: sc.count.toLocaleString(), color: sc.color, onClick: makeStarNav(sc.starKey) });
    }
    const accPct = stats.avgAccuracy / 10000;
    const isGoldAcc = accPct >= 100 && stats.fcPercent === '100.0';
    const accColor = stats.avgAccuracy > 0 ? (isGoldAcc ? Colors.gold : accuracyColor(accPct)) : undefined;
    cards.push({ label: t('player.avgAccuracy'), value: stats.avgAccuracy > 0 ? formatClamped(accPct) + '%' : '—', color: accColor });
    cards.push({ label: t('player.avgStars'), value: stats.averageStars === 6 ? <GoldStars /> : (stats.averageStars > 0 ? formatClamped2(stats.averageStars) : '—') });
    cards.push({ label: t('player.bestRank'), value: stats.bestRank > 0 ? `#${stats.bestRank.toLocaleString()}` : '—', onClick: stats.bestRankSongId ? () => withProfileSwitch(() => navigate(`/songs/${stats.bestRankSongId}?instrument=${encodeURIComponent(inst)}`, { state: { backTo: location.pathname, autoScroll: true } })) : undefined });
    const pctGold = (v: string) => /^Top [1-5]%$/.test(v) ? Colors.gold : undefined;
    cards.push({ label: t('player.percentile'), value: stats.overallPercentile, color: pctGold(stats.overallPercentile), onClick: () => {
      withProfileSwitch(() => {
        const s = loadSongSettings();
        saveSongSettings({ ...s, instrument: inst, sortMode: 'percentile', sortAscending: true, filters: cleanFilters(s) });
        navigate('/songs', { state: { backTo: location.pathname, restagger: true } });
      });
    } });
    cards.push({ label: t('player.songsPlayed'), value: stats.avgPercentile, color: pctGold(stats.avgPercentile), onClick: () => {
      withProfileSwitch(() => {
        const s = loadSongSettings();
        saveSongSettings({ ...s, instrument: inst, sortMode: 'percentile', sortAscending: true, filters: { ...cleanFilters(s), hasScores: { ...s.filters.hasScores, [inst]: true } } });
        navigate('/songs', { state: { backTo: location.pathname, restagger: true } });
      });
    } });

    for (let ci = 0; ci < cards.length; ci++) {
      const c = cards[ci];
      items.push({ key: `${inst}-card-${ci}`, span: false, heightEstimate: 100, style: cardStyle, node: <StatBox label={c.label} value={c.value} color={c.color} onClick={c.onClick} /> });
    }

    // Percentile table — single glass container
    if (stats.percentileBuckets.length > 0) {
      const thresholds = [1,2,3,4,5,10,15,20,25,30,40,50,60,70,80,90,100];
      items.push({
        key: `${inst}-pct-table`,
        span: true,
        heightEstimate: 40 + stats.percentileBuckets.length * 44,
        style: { ...cardStyle, overflow: 'hidden' as const, marginBottom: Gap.md },
        node: (
          <div>
            <div className={s.pctRowHeader}>
              <span className={s.pctHeaderText}>{t('player.percentileHeader')}</span>
              <span style={{ ...s.pctHeaderText, textAlign: 'right' }}>{t('player.songsHeader')}</span>
            </div>
            {stats.percentileBuckets.map((b, pi) => {
              const isLast = pi === stats.percentileBuckets.length - 1;
              const isTop1 = b.pct <= 1;
              const isGold = b.pct <= 5;
              const badgeStyle = isTop1 ? s.pctGoldBadge : isGold ? s.pctGoldPill : undefined;
              return (
                <div
                  key={b.pct}
                  className={s.pctRowItem} style={{ ...(isLast ? { borderBottom: 'none' } : {}) }}
                  onClick={() => {
                    withProfileSwitch(() => {
                      const s = loadSongSettings();
                      const percentileFilter: Record<number, boolean> = {};
                      for (const t of thresholds) percentileFilter[t] = t === b.pct;
                      percentileFilter[0] = false;
                      saveSongSettings({ ...s, instrument: inst, sortAscending: true, filters: { ...cleanFilters(s), percentileFilter } });
                      navigate('/songs', { state: { backTo: location.pathname, restagger: true } });
                    });
                  }}
                >
                  <span>
                    {badgeStyle
                      ? <span style={badgeStyle}>Top {b.pct}%</span>
                      : <span className={s.pctPlainLabel}>Top {b.pct}%</span>}
                  </span>
                  <span style={{ fontWeight: 600 }}>{b.count}</span>
                </div>
              );
            })}
          </div>
        ),
      });
    }
  }

  // --- Top Songs heading ---
  items.push({
    key: 'top-heading',
    span: true,
    heightEstimate: 80,
    node: (
      <div style={{ marginTop: Gap.section }}>
        <h2 className={s.sectionTitle}>{t('player.topSongsPerInstrument')}</h2>
        <p className={s.sectionDesc}>{data.displayName}'s highest and lowest-ranked competitive songs per instrument, sorted by percentile.</p>
      </div>
    ),
  });

  // --- Top/Bottom song rows (each is its own grid item) ---
  for (const inst of visibleKeys) {
    const scores = byInstrument.get(inst);
    if (!scores || scores.length === 0) continue;
    const withPct = scores.filter((s) => s.rank > 0 && (s.totalEntries ?? 0) > 0);
    if (withPct.length === 0) continue;
    const sorted = withPct.slice().sort((a, b) => a.rank / a.totalEntries! - b.rank / b.totalEntries!);
    const topScores = sorted.slice(0, 5);
    const bottomScores = sorted.length > 5 ? sorted.slice(-5).reverse() : [];

    const renderSongRow = (s: typeof topScores[0], _isLast: boolean) => {
      const song = songMap.get(s.songId);
      const pct = s.rank > 0 && (s.totalEntries ?? 0) > 0
        ? Math.min((s.rank / s.totalEntries!) * 100, 100)
        : undefined;
      const handleClick = (e: React.MouseEvent) => {
        e.preventDefault();
        withProfileSwitch(() => navigate(`/songs/${s.songId}?instrument=${encodeURIComponent(inst)}`, { state: { backTo: location.pathname, autoScroll: true } }));
      };
      return (
        <a key={s.songId} href={`#/songs/${s.songId}?instrument=${encodeURIComponent(inst)}`} onClick={handleClick} style={s.songListRow}>
          <AlbumArt src={song?.albumArt} size={Size.thumb} />
          <div className={s.topSongText}>
            <span className={s.topSongName}>{song?.title ?? s.songId.slice(0, 8)}</span>
            <span className={s.topSongArtist}>{song?.artist ?? ''}{song?.year ? ` · ${song.year}` : ''}</span>
          </div>
          <div className={s.topSongRight}>
            {pct != null && (() => {
              const isTop1 = pct <= 1;
              const isTop5 = pct <= 5;
              const pctStyle = isTop1
                ? s.percentileBadgeTop1
                : isTop5
                  ? s.percentileBadgeTop5
                  : s.percentilePill;
              return <span style={pctStyle}>{formatPercentileBucket(pct)}</span>;
            })()}
          </div>
        </a>
      );
    };

    // Top songs header
    items.push({
      key: `top-hdr-${inst}`,
      span: true,
      heightEstimate: 64,
      node: (
        <div className={s.instCardHeader}>
          <InstrumentIcon instrument={inst} size={48} />
          <div style={{ display: 'flex', flexDirection: 'column', justifyContent: 'center', height: 48 }}>
            <span className={s.instCardTitle}>{t('player.topFiveSongs')}</span>
            <span style={{ ...s.sectionDesc, margin: 0, fontSize: Font.md }}>{`${data.displayName}'s highest-ranked songs for ${INSTRUMENT_LABELS[inst]}.`}</span>
          </div>
        </div>
      ),
    });

    // Top songs table
    items.push({
      key: `top-songs-${inst}`,
      span: true,
      heightEstimate: topScores.length * 72,
      node: (
        <div style={{ display: 'flex', flexDirection: 'column', gap: Gap.sm, marginBottom: Gap.section }}>
          {topScores.map((s, si) => renderSongRow(s, si === topScores.length - 1))}
        </div>
      ),
    });

    if (bottomScores.length > 0) {
      // Bottom songs header
      items.push({
        key: `bot-hdr-${inst}`,
        span: true,
        heightEstimate: 64,
        node: (
          <div style={{ ...s.instCardHeader, marginTop: Gap.md }}>
            <InstrumentIcon instrument={inst} size={48} />
            <div style={{ display: 'flex', flexDirection: 'column', justifyContent: 'center', height: 48 }}>
              <span className={s.instCardTitle}>{t('player.bottomFiveSongs')}</span>
              <span style={{ ...s.sectionDesc, margin: 0, fontSize: Font.md }}>{`${data.displayName}'s lowest-ranked songs for ${INSTRUMENT_LABELS[inst]}.`}</span>
            </div>
          </div>
        ),
      });

      // Bottom songs table
      items.push({
        key: `bot-songs-${inst}`,
        span: true,
        heightEstimate: bottomScores.length * 72,
        node: (
          <div style={{ display: 'flex', flexDirection: 'column', gap: Gap.sm, marginBottom: Gap.section }}>
            {bottomScores.map((s, si) => renderSongRow(s, si === bottomScores.length - 1))}
          </div>
        ),
      });
    }
  }

  // Wire up container-level scroll fade
  const fadeDeps = useMemo(() => [items.length], [items.length]);
  const updateFade = useScrollMask(scrollRef, fadeDeps);
  const hasFab = useIsMobile();
  const rushOnScroll = useStaggerRush(scrollRef);

  const handleScroll = useCallback(() => {
    updateFade();
    rushOnScroll();
  }, [updateFade, rushOnScroll]);

  const [isNarrowGrid, setIsNarrowGrid] = useState(() => hasFab && typeof window !== 'undefined' && window.innerWidth < 400);
  useEffect(() => {
    if (!hasFab) return;
    const mql = window.matchMedia('(max-width: 399px)');
    const handler = () => setIsNarrowGrid(mql.matches);
    mql.addEventListener('change', handler);
    handler();
    return () => mql.removeEventListener('change', handler);
  }, [hasFab]);

  // Only render the button container on desktop non-PWA; animate visibility
  const canShowSelectBtn = !hasFab && !IS_IOS && !IS_ANDROID && !IS_PWA;
  const selectBtnVisible = canShowSelectBtn && !isTrackedPlayer && trackedPlayer?.accountId !== data.accountId;

  return (
    <div className={s.page}>
      <div className={s.playerNameBar}>
        <h1 className={s.playerName}>{data.displayName}</h1>
        {canShowSelectBtn && (
          <button
            style={{
              ...s.selectProfileBtn,
              opacity: selectBtnVisible ? 1 : 0,
              transform: selectBtnVisible ? 'scale(1)' : 'scale(0.9)',
              pointerEvents: selectBtnVisible ? 'auto' as const : 'none' as const,
              transition: 'opacity 300ms ease, transform 300ms ease',
            }}
            onClick={() => {
              if (trackedPlayer && trackedPlayer.accountId !== data.accountId) {
                setPendingSwitch(() => () => setPlayer({ accountId: data.accountId, displayName: data.displayName }));
              } else {
                setPlayer({ accountId: data.accountId, displayName: data.displayName });
              }
            }}
          >
            <IoPerson size={16} style={{ marginRight: Gap.md }} />
            Select Player Profile
          </button>
        )}
      </div>
      <div ref={scrollRef} onScroll={handleScroll} className={s.scrollArea}>
        <div style={{ ...s.container, ...(hasFab ? { paddingBottom: 72 } : {}) }}>
          <div className={s.gridList} style={{ ...(isNarrowGrid ? { gridTemplateColumns: 'minmax(0, 1fr)' } : {}) }}>
            {(() => {
              // Compute which items are in the initial viewport by accumulating
              // estimated row heights.  The grid is 2-col: span items take a full
              // row, non-span items pair up (each row = max of the pair's height).
              const vh = typeof window !== 'undefined' ? window.innerHeight : 900;
              const gap = 8; // Gap.md
              let accHeight = 0;
              let col = 0; // 0 = left, 1 = right in the 2-col grid
              let rowMax = 0;
              let visibleCount = items.length; // default: animate all

              for (let i = 0; i < items.length; i++) {
                const item = items[i];
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

              const lastVisibleDelay = visibleCount * 80;
              return items.map((item, i) => {
                const delay = skipAnim ? undefined : (i < visibleCount ? (i + 1) * 80 : lastVisibleDelay);
                return (
                  <FadeInDiv key={item.key} delay={delay} style={{ ...(item.span ? s.gridFullWidth : {}), ...item.style }}>
                    {item.node}
                  </FadeInDiv>
                );
              });
            })()}
          </div>
        </div>
      </div>
      {pendingSwitch && (
        <ConfirmAlert
          title={`Switch to ${data.displayName}`}
          message={`In order to see ${data.displayName}'s scores, you will have to set them as your selected profile. Would you like to continue?`}
          onNo={() => setPendingSwitch(null)}
          onYes={() => { setPendingSwitch(null); pendingSwitch(); }}
        />
      )}
    </div>
  );
}

function GoldStars() {
  return (
    <span style={{ display: 'inline-flex', gap: 2 }}>
      {Array.from({ length: 5 }, (_, i) => (
        <img key={i} src={`${import.meta.env.BASE_URL}star_gold.png`} alt="★" width={18} height={18} />
      ))}
    </span>
  );
}

// ─── Computation helpers ────────────────────────────────────

// ─── Styles ─────────────────────────────────────────────────

