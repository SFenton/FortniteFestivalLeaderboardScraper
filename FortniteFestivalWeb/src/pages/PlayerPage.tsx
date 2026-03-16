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
import { InstrumentIcon } from '../components/InstrumentIcons';
import { useSettings, isInstrumentVisible } from '../contexts/SettingsContext';
import { loadSongSettings, saveSongSettings, defaultSongFilters } from '../components/songSettings';
import { useScrollMask } from '../hooks/useScrollMask';
import { useIsMobile } from '../hooks/useIsMobile';
import { IS_IOS, IS_ANDROID, IS_PWA } from '../utils/platform';
import { useTrackedPlayer } from '../hooks/useTrackedPlayer';
import { useScoreFilter } from '../hooks/useScoreFilter';
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

  if (loading) return <div style={styles.page}><div style={styles.center}><div style={styles.arcSpinner} /></div></div>;
  if (error) return <div style={styles.page}><div style={styles.centerError}>{error}</div></div>;
  if (!data) return <div style={styles.page}><div style={styles.center}>{t('player.playerNotFound')}</div></div>;

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
        <div style={styles.syncBanner}>
          <div style={styles.syncSpinner} />
          <div style={{ flex: 1 }}>
            <div style={styles.syncTitle}>
              {syncPhase === 'backfill' ? t('player.syncInProgress') : t('player.syncInProgress')}
            </div>
            <div style={styles.syncSubtitle}>
              {syncPhase === 'backfill'
                ? `Syncing ${data.displayName}'s scores…`
                : `Reconstructing ${data.displayName}'s score history across seasons…`}
            </div>
            {syncPhase === 'backfill' && backfillProgress > 0 && (
              <div style={{ marginTop: Gap.md }}>
                <div style={styles.syncProgressLabel}>
                  <span>{t('player.syncingScores')}</span>
                  <span>{(backfillProgress * 100).toFixed(1)}%</span>
                </div>
                <div style={styles.syncProgressOuter}>
                  <div style={{ ...styles.syncProgressInner, width: `${Math.round(backfillProgress * 100)}%` }} />
                </div>
              </div>
            )}
            {syncPhase === 'history' && (
              <>
                <div style={{ marginTop: Gap.md }}>
                  <div style={styles.syncProgressLabel}>
                    <span>{t('player.syncingScores')}</span><span>100.0%</span>
                  </div>
                  <div style={styles.syncProgressOuter}>
                    <div style={{ ...styles.syncProgressInner, width: '100%' }} />
                  </div>
                </div>
                {historyProgress > 0 && (
                  <div style={{ marginTop: Gap.sm }}>
                    <div style={styles.syncProgressLabel}>
                      <span>{t('player.buildingHistory')}</span>
                      <span>{(historyProgress * 100).toFixed(1)}%</span>
                    </div>
                    <div style={styles.syncProgressOuter}>
                      <div style={{ ...styles.syncProgressInner, width: `${Math.round(historyProgress * 100)}%` }} />
                    </div>
                  </div>
                )}
              </>
            )}
          </div>
        </div>
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
        <h2 style={styles.sectionTitle}>{t('player.instrumentStats')}</h2>
        <p style={styles.sectionDesc}>A quick look at {data.displayName}'s overall Festival statistics per instrument.</p>
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
        <div style={styles.instCardHeader}>
          <InstrumentIcon instrument={inst} size={48} />
          <span style={styles.instCardTitle}>{INSTRUMENT_LABELS[inst]}</span>
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
            <div style={styles.pctRowHeader}>
              <span style={styles.pctHeaderText}>{t('player.percentileHeader')}</span>
              <span style={{ ...styles.pctHeaderText, textAlign: 'right' }}>{t('player.songsHeader')}</span>
            </div>
            {stats.percentileBuckets.map((b, pi) => {
              const isLast = pi === stats.percentileBuckets.length - 1;
              const isTop1 = b.pct <= 1;
              const isGold = b.pct <= 5;
              const badgeStyle = isTop1 ? styles.pctGoldBadge : isGold ? styles.pctGoldPill : undefined;
              return (
                <div
                  key={b.pct}
                  style={{ ...styles.pctRowItem, ...(isLast ? { borderBottom: 'none' } : {}) }}
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
                      : <span style={styles.pctPlainLabel}>Top {b.pct}%</span>}
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
        <h2 style={styles.sectionTitle}>{t('player.topSongsPerInstrument')}</h2>
        <p style={styles.sectionDesc}>{data.displayName}'s highest and lowest-ranked competitive songs per instrument, sorted by percentile.</p>
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
        <a key={s.songId} href={`#/songs/${s.songId}?instrument=${encodeURIComponent(inst)}`} onClick={handleClick} style={styles.songListRow}>
          <AlbumArt src={song?.albumArt} size={Size.thumb} />
          <div style={styles.topSongText}>
            <span style={styles.topSongName}>{song?.title ?? s.songId.slice(0, 8)}</span>
            <span style={styles.topSongArtist}>{song?.artist ?? ''}{song?.year ? ` · ${song.year}` : ''}</span>
          </div>
          <div style={styles.topSongRight}>
            {pct != null && (() => {
              const isTop1 = pct <= 1;
              const isTop5 = pct <= 5;
              const pctStyle = isTop1
                ? styles.percentileBadgeTop1
                : isTop5
                  ? styles.percentileBadgeTop5
                  : styles.percentilePill;
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
        <div style={styles.instCardHeader}>
          <InstrumentIcon instrument={inst} size={48} />
          <div style={{ display: 'flex', flexDirection: 'column', justifyContent: 'center', height: 48 }}>
            <span style={styles.instCardTitle}>{t('player.topFiveSongs')}</span>
            <span style={{ ...styles.sectionDesc, margin: 0, fontSize: Font.md }}>{`${data.displayName}'s highest-ranked songs for ${INSTRUMENT_LABELS[inst]}.`}</span>
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
          <div style={{ ...styles.instCardHeader, marginTop: Gap.md }}>
            <InstrumentIcon instrument={inst} size={48} />
            <div style={{ display: 'flex', flexDirection: 'column', justifyContent: 'center', height: 48 }}>
              <span style={styles.instCardTitle}>{t('player.bottomFiveSongs')}</span>
              <span style={{ ...styles.sectionDesc, margin: 0, fontSize: Font.md }}>{`${data.displayName}'s lowest-ranked songs for ${INSTRUMENT_LABELS[inst]}.`}</span>
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
    <div style={styles.page}>
      <div style={styles.playerNameBar}>
        <h1 style={styles.playerName}>{data.displayName}</h1>
        {canShowSelectBtn && (
          <button
            style={{
              ...styles.selectProfileBtn,
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
      <div ref={scrollRef} onScroll={handleScroll} style={styles.scrollArea}>
        <div style={{ ...styles.container, ...(hasFab ? { paddingBottom: 72 } : {}) }}>
          <div style={{ ...styles.gridList, ...(isNarrowGrid ? { gridTemplateColumns: 'minmax(0, 1fr)' } : {}) }}>
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
                  <FadeInDiv key={item.key} delay={delay} style={{ ...(item.span ? styles.gridFullWidth : {}), ...item.style }}>
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

function StatBox({ label, value, color, onClick }: { label: string; value: React.ReactNode; color?: string; onClick?: () => void }) {
  const inner = (
    <div style={styles.statBox}>
      <span style={{ ...styles.statValue, ...(color ? { color } : {}) }}>{value}</span>
      <span style={styles.statLabel}>{label}</span>
    </div>
  );
  if (onClick) return (
    <div onClick={onClick} style={{ cursor: 'pointer', position: 'relative' as const }}>
      {inner}
      <svg style={styles.statChevron} width="8" height="14" viewBox="0 0 8 14" fill="none"><path d="M1.5 1.5L6.5 7L1.5 12.5" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/></svg>
    </div>
  );
  return inner;
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

const styles: Record<string, React.CSSProperties> = {
  page: {
    height: '100%',
    display: 'flex',
    flexDirection: 'column' as const,
    color: Colors.textPrimary,
    fontFamily:
      "-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif",
  },
  scrollArea: {
    flex: 1,
    overflowY: 'auto' as const,
    overflowX: 'hidden' as const,
  },
  container: {
    maxWidth: MaxWidth.card,
    margin: '0 auto',
    padding: `${Layout.paddingTop}px ${Layout.paddingHorizontal}px`,
    boxSizing: 'border-box' as const,
    width: '100%',
  },
  playerNameBar: {
    maxWidth: MaxWidth.card,
    margin: '0 auto',
    width: '100%',
    padding: `${Gap.lg}px ${Layout.paddingHorizontal}px 0`,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    boxSizing: 'border-box' as const,
    minHeight: 48 + Gap.lg,
  },
  playerName: {
    fontSize: 28,
    fontWeight: 700,
    margin: 0,
  },
  subtitle: {
    fontSize: Font.md,
    color: Colors.textSubtle,
    marginBottom: Gap.section,
  },
  sectionTitle: {
    fontSize: Font.xl,
    fontWeight: 800,
    color: Colors.textPrimary,
    marginBottom: Gap.xs,
    marginTop: 0,
  },
  sectionDesc: {
    fontSize: Font.sm,
    color: Colors.textSecondary,
    marginBottom: Gap.xl,
    marginTop: 0,
    wordWrap: 'break-word' as const,
  },
  catDesc: {
    fontSize: Font.xs,
    color: Colors.textSecondary,
    marginTop: Gap.xs,
    opacity: 0.85,
  },
  syncBanner: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.xl,
    padding: `${Gap.xl}px ${Gap.section}px`,
    backgroundColor: Colors.accentPurpleDark,
    border: `1px solid ${Colors.borderPrimary}`,
    borderRadius: Radius.lg,
    marginBottom: Gap.section,
  },
  syncSpinner: {
    width: 24,
    height: 24,
    border: '3px solid rgba(255,255,255,0.15)',
    borderTopColor: Colors.accentPurple,
    borderRadius: '50%',
    animation: 'spin 0.8s linear infinite',
    flexShrink: 0,
  },
  syncTitle: {
    fontSize: Font.lg,
    fontWeight: 700,
    color: Colors.textPrimary,
    marginBottom: Gap.xs,
  },
  syncSubtitle: {
    fontSize: Font.sm,
    color: Colors.textSecondary,
  },
  syncProgressOuter: {
    marginTop: Gap.xs,
    height: 6,
    backgroundColor: 'rgba(255,255,255,0.1)',
    borderRadius: 3,
    overflow: 'hidden',
  },
  syncProgressInner: {
    height: '100%',
    backgroundColor: Colors.accentPurple,
    borderRadius: 3,
    transition: 'width 0.3s ease',
  },
  syncProgressLabel: {
    display: 'flex',
    justifyContent: 'space-between',
    fontSize: Font.xs,
    color: Colors.textSecondary,
    marginBottom: Gap.xs,
  },
  // Overall summary
  summaryGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, 1fr)',
    gap: Gap.md,
    marginBottom: Gap.section * 1.5,
  },
  statBox: {
    display: 'flex',
    flexDirection: 'column' as const,
    alignItems: 'center',
    padding: `${Gap.xl}px ${Gap.md}px`,
    minWidth: 0,
    overflow: 'hidden',
  },
  statValue: {
    fontSize: Font.xl,
    fontWeight: 700,
    color: Colors.accentBlueBright,
    marginBottom: Gap.xs,
    wordBreak: 'break-word' as const,
    textAlign: 'center' as const,
  },
  statLabel: {
    fontSize: Font.xs,
    color: Colors.textTertiary,
    textTransform: 'uppercase' as const,
    letterSpacing: 0.5,
  },
  statChevron: {
    position: 'absolute' as const,
    right: Gap.xl,
    top: '50%',
    transform: 'translateY(-50%)',
    color: Colors.textPrimary,
  },
  selectProfileBtn: {
    ...frostedCard,
    backgroundColor: 'rgb(124,58,237)',
    border: '1px solid rgba(168,120,255,0.3)',
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    padding: `0 ${Gap.section + 8}px 0 ${Gap.section}px`,
    borderRadius: Radius.full,
    color: '#fff',
    fontSize: Font.lg,
    fontWeight: 600,
    cursor: 'pointer',
    flexShrink: 0,
    height: 48,
    whiteSpace: 'nowrap' as const,
  },
  // Per-instrument cards
  gridList: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: Gap.md,
    minWidth: 0,
    overflow: 'hidden',
  },
  gridFullWidth: {
    gridColumn: '1 / -1',
  },
  instCard: {
    ...frostedCard,
    borderRadius: Radius.lg,
    overflow: 'hidden',
  },
  instCardHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.md,
    paddingBottom: Gap.sm,
  },
  instCardTitle: {
    fontSize: Font.xl,
    fontWeight: 600,
  },
  instCardSubtitle: {
    fontSize: Font.sm,
    color: Colors.textSecondary,
  },
  instCardBody: {
    padding: Gap.xl,
  },
  // Per-instrument stat card grid
  instSummaryGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, 1fr)',
    gap: Gap.md,
    marginBottom: Gap.xl,
  },
  // Percentile table
  pctTablePanel: {
    ...frostedCard,
    borderRadius: Radius.lg,
    overflow: 'hidden',
    marginBottom: Gap.xl,
  },
  pctTable: {
    width: '100%',
    borderCollapse: 'collapse' as const,
  },
  pctRow: {
    cursor: 'pointer',
    transition: 'background-color 0.15s',
  },
  pctTh: {
    padding: `${Gap.xl}px ${Gap.xl}px`,
    fontSize: Font.sm,
    fontWeight: 600,
    color: Colors.textTertiary,
    textTransform: 'uppercase' as const,
    letterSpacing: 0.5,
    borderBottom: `1px solid ${Colors.glassBorder}`,
    textAlign: 'left' as const,
  },
  pctTd: {
    padding: `${Gap.xl}px ${Gap.xl}px`,
    fontSize: Font.md,
    color: Colors.textPrimary,
    borderBottom: `1px solid ${Colors.glassBorder}`,
  },
  pctGoldBadge: goldOutlineSkew,
  pctGoldPill: goldOutline,
  pctPlainLabel: {
    padding: `${Gap.xs}px ${Gap.sm}px`,
    border: '2px solid transparent',
    display: 'inline-block',
    fontWeight: 600,
  },
  pctRowHeader: {
    display: 'flex',
    justifyContent: 'space-between',
    padding: `${Gap.md}px ${Gap.xl}px`,
    borderBottom: `1px solid ${Colors.glassBorder}`,
  },
  pctHeaderText: {
    fontSize: Font.sm,
    fontWeight: 600,
    color: Colors.textTertiary,
    textTransform: 'uppercase' as const,
    letterSpacing: 0.5,
  },
  pctRowItem: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
    padding: `${Gap.md}px ${Gap.xl}px`,
    borderBottom: `1px solid ${Colors.glassBorder}`,
    cursor: 'pointer',
    transition: 'background-color 0.15s',
    fontSize: Font.md,
    color: Colors.textPrimary,
    minWidth: 0,
  },
  // Top songs
  topSongs: {
    borderTop: `1px solid ${Colors.borderSubtle}`,
    paddingTop: Gap.xl,
  },
  topSongsTitle: {
    fontSize: Font.sm,
    fontWeight: 600,
    color: Colors.textSecondary,
    textTransform: 'uppercase' as const,
    letterSpacing: 0.5,
    display: 'block',
    marginBottom: Gap.md,
  },
  topSongRowGlass: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.md,
    padding: `${Gap.sm}px ${Gap.xl}px`,
    textDecoration: 'none',
    color: 'inherit',
  },
  songListHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.md,
    padding: `${Gap.md}px ${Gap.xl}px`,
    borderBottom: `1px solid ${Colors.glassBorder}`,
  },
  songListTitle: {
    display: 'block',
    fontSize: Font.md,
    fontWeight: 600,
  },
  songListSubtitle: {
    display: 'block',
    fontSize: Font.xs,
    color: Colors.textSecondary,
  },
  songListRow: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.xl,
    padding: `0 ${Gap.xl}px`,
    height: 64,
    borderRadius: Radius.md,
    ...frostedCard,
    textDecoration: 'none',
    color: 'inherit',
    transition: 'background-color 0.15s',
  },
  topSongRow: {
    display: 'flex',
    alignItems: 'center',
    gap: Gap.md,
    padding: `${Gap.sm}px 0`,
    textDecoration: 'none',
    color: 'inherit',
  },
  topSongThumb: {
    width: Size.thumb,
    height: Size.thumb,
    borderRadius: Radius.xs,
    objectFit: 'cover' as const,
    flexShrink: 0,
  },
  topSongText: {
    flex: 1,
    minWidth: 0,
    display: 'flex',
    flexDirection: 'column' as const,
  },
  topSongName: {
    fontSize: Font.md,
    fontWeight: 600,
    whiteSpace: 'nowrap' as const,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
  },
  topSongArtist: {
    fontSize: Font.sm,
    color: Colors.textSubtle,
    whiteSpace: 'nowrap' as const,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
  },
  topSongRight: {
    textAlign: 'right' as const,
    flexShrink: 0,
  },
  topSongScore: {
    fontSize: Font.sm,
    fontWeight: 600,
    color: Colors.accentBlueBright,
    fontVariantNumeric: 'tabular-nums',
    display: 'block',
  },
  topSongMeta: {
    fontSize: Font.xs,
    color: Colors.gold,
  },
  percentilePill: {
    fontSize: Font.lg,
    fontWeight: 600,
    color: Colors.textSecondary,
    backgroundColor: 'rgba(255,255,255,0.1)',
    padding: `${Gap.xs}px ${Gap.md}px`,
    borderRadius: Radius.xs,
    textAlign: 'center' as const,
    display: 'inline-block',
  },
  percentileBadgeTop1: {
    ...goldOutlineSkew,
    fontSize: Font.lg,
    textAlign: 'center' as const,
  },
  percentileBadgeTop5: {
    ...goldOutline,
    fontSize: Font.lg,
    textAlign: 'center' as const,
  },
  percentilePillGold: goldFill,
  accuracyPill: {
    fontSize: Font.lg,
    fontWeight: 600,
    color: Colors.textSecondary,
    backgroundColor: 'rgba(255,255,255,0.1)',
    padding: `${Gap.xs}px ${Gap.md}px`,
    borderRadius: Radius.xs,
    minWidth: 70,
    textAlign: 'center' as const,
    display: 'inline-block',
  },
  accuracyBadgeFC: {
    ...goldOutlineSkew,
    fontSize: Font.lg,
    textAlign: 'center' as const,
  },
  center: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    flex: 1,
  },
  arcSpinner: {
    width: 48,
    height: 48,
    border: '4px solid rgba(255,255,255,0.10)',
    borderTopColor: Colors.accentPurple,
    borderRadius: '50%',
    animation: 'spin 0.8s linear infinite',
  },
  centerError: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    minHeight: '100vh',
    color: Colors.statusRed,
    backgroundColor: Colors.backgroundApp,
    fontSize: Font.lg,
  },
};
