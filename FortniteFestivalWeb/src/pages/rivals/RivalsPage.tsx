/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useEffect, useState, useCallback, useRef, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { api } from '../../api/client';
import { useSettings, visibleInstruments } from '../../contexts/SettingsContext';
import { usePageTransition } from '../../hooks/ui/usePageTransition';
import { useStagger } from '../../hooks/ui/useStagger';
import EmptyState from '../../components/common/EmptyState';
import PageHeader from '../../components/common/PageHeader';

import { useTrackedPlayer } from '../../hooks/data/useTrackedPlayer';
import InstrumentHeader from '../../components/display/InstrumentHeader';
import { useIsMobileChrome } from '../../hooks/ui/useIsMobile';
import { IoChevronForward, IoMusicalNotes, IoTrophy } from 'react-icons/io5';
import { InstrumentHeaderSize } from '@festival/core';
import { LoadPhase } from '@festival/core';
import { Gap, Size, flexColumn } from '@festival/theme';
import { serverInstrumentLabel, type RivalsListResponse, type ServerInstrumentKey } from '@festival/core/api/serverTypes';
import type { RivalSummary } from '@festival/core/api/serverTypes';
import { deriveComboFromSettings } from './helpers/comboUtils';
import RivalRow from './components/RivalRow';
import { Routes } from '../../routes';
import fx from '../../styles/effects.module.css';
import { useRivalsSharedStyles } from './useRivalsSharedStyles';
import Page from '../Page';
import { rivalsSlides } from './firstRun';
import LeaderboardRivalsTab from './LeaderboardRivalsTab';
import { ActionPill } from '../../components/common/ActionPill';
import { useFabSearch } from '../../contexts/FabSearchContext';

// Module-level data cache so back-navigation has instant data
let _cachedInstrumentRivals: InstrumentRivals[] = [];
let _cachedComboRivals: RivalsListResponse | null = null;
let _cachedComputedAt: string | null = null;
let _cachedAccountId: string | null = null;

type InstrumentRivals = {
  instrument: ServerInstrumentKey;
  data: RivalsListResponse | null;
  loading: boolean;
  error: string | null;
};

export default function RivalsPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const { settings } = useSettings();
  const { player } = useTrackedPlayer();
  const isMobile = useIsMobileChrome();
  const accountId = player?.accountId;
  const fabSearch = useFabSearch();

  const activeTab = (searchParams.get('tab') === 'leaderboard' ? 'leaderboard' : 'song') as 'song' | 'leaderboard';
  const setTab = useCallback((tab: 'song' | 'leaderboard') => {
    setSearchParams(tab === 'song' ? {} : { tab }, { replace: true });
  }, [setSearchParams]);

  const toggleTab = useCallback(() => {
    setTab(activeTab === 'song' ? 'leaderboard' : 'song');
  }, [activeTab, setTab]);

  const activeInstruments = visibleInstruments(settings);
  const combo = useMemo(() => deriveComboFromSettings(settings), [settings]);

  // Data state: initialize from cache when returning to same account
  const hasCachedData = accountId === _cachedAccountId && _cachedInstrumentRivals.length > 0;

  const [instrumentRivals, setInstrumentRivals] = useState<InstrumentRivals[]>(hasCachedData ? _cachedInstrumentRivals : []);
  const [comboRivals, setComboRivals] = useState<RivalsListResponse | null>(hasCachedData ? _cachedComboRivals : null);
  const [comboLoading, setComboLoading] = useState(false);
  const [computedAt, setComputedAt] = useState<string | null>(hasCachedData ? _cachedComputedAt : null);
  const [, setPlayerName] = useState<string | null>(null);

  // Register toggle action for FAB and sync active tab
  const toggleTabRef = useRef(toggleTab);
  toggleTabRef.current = toggleTab;
  /* v8 ignore start — FAB registration */
  useEffect(() => {
    fabSearch.registerRivalsActions({ toggleTab: () => toggleTabRef.current() });
  }, [fabSearch]);
  useEffect(() => {
    fabSearch.setRivalsActiveTab(activeTab);
  }, [fabSearch, activeTab]);
  /* v8 ignore stop */

  // Resolve player display name
  /* v8 ignore start — async data fetch */
  useEffect(() => {
    if (!accountId) return;
    if (player?.accountId === accountId) {
      setPlayerName(player.displayName);
      return;
    }
    let cancelled = false;
    api.getPlayer(accountId).then(res => {
      if (!cancelled) setPlayerName(res.displayName);
    }).catch(() => { /* ignored */ });
    return () => { cancelled = true; };
  }, [accountId, player]);
  /* v8 ignore stop */

  // Fetch overview for computedAt timestamp
  /* v8 ignore start — async data fetch */
  useEffect(() => {
    if (!accountId || hasCachedData) return;
    let cancelled = false;
    api.getRivalsOverview(accountId).then(res => {
      if (!cancelled) setComputedAt(res.computedAt);
    }).catch(() => { /* ignored */ });
    return () => { cancelled = true; };
  }, [accountId]);
  /* v8 ignore stop */

  // Fetch per-instrument rivals
  /* v8 ignore start — async data fetch */
  useEffect(() => {
    if (!accountId) return;
    // Skip re-fetch on back-nav when cached data exists
    if (hasCachedData) return;
    let cancelled = false;

    const entries: InstrumentRivals[] = activeInstruments.map(inst => ({
      instrument: inst,
      data: null,
      loading: true,
      error: null,
    }));
    setInstrumentRivals(entries);

    activeInstruments.forEach((inst, idx) => {
      api.getRivalsList(accountId, inst).then(res => {
        if (cancelled) return;
        setInstrumentRivals(prev => {
          const next = [...prev];
          next[idx] = { instrument: inst, data: res, loading: false, error: null };
          return next;
        });
      }).catch(err => {
        if (cancelled) return;
        setInstrumentRivals(prev => {
          const next = [...prev];
          next[idx] = { instrument: inst, data: null, loading: false, error: err instanceof Error ? err.message : 'Error' };
          return next;
        });
      });
    });

    return () => { cancelled = true; };
  // eslint-disable-next-line react-hooks/exhaustive-deps -- activeInstruments derived from settings
  }, [accountId, settings.showLead, settings.showBass, settings.showDrums, settings.showVocals, settings.showProLead, settings.showProBass]);
  /* v8 ignore stop */

  // Fetch combo rivals
  /* v8 ignore start — async data fetch */
  useEffect(() => {
    if (!accountId || !combo) {
      setComboRivals(null);
      setComboLoading(false);
      return;
    }
    // Skip re-fetch on back-nav when cached data exists
    if (hasCachedData) return;
    let cancelled = false;
    setComboLoading(true);

    api.getRivalsList(accountId, combo).then(res => {
      if (!cancelled) { setComboRivals(res); setComboLoading(false); }
    }).catch(() => {
      if (!cancelled) {
        setComboLoading(false);
      }
    });
    return () => { cancelled = true; };
  }, [accountId, combo]);
  /* v8 ignore stop */

  const allInstrumentsReady = instrumentRivals.length > 0 && instrumentRivals.every(r => !r.loading);
  const comboReady = !combo || !comboLoading;
  const allReady = allInstrumentsReady && comboReady;

  // Persist data to module-level cache for instant back-nav
  useEffect(() => {
    if (!allReady || !accountId) return;
    _cachedAccountId = accountId;
    _cachedInstrumentRivals = instrumentRivals;
    _cachedComboRivals = comboRivals;
    _cachedComputedAt = computedAt;
  }, [allReady, accountId, instrumentRivals, comboRivals, computedAt]);

  // Common rivals: rivals that appear in ALL loaded instrument lists (2+ instruments)
  /* v8 ignore start -- common rivals intersection logic */
  const commonRivals = useMemo<{ above: RivalSummary[]; below: RivalSummary[] }>(() => {
    const loaded = instrumentRivals.filter(r => r.data);
    if (loaded.length < 2) return { above: [], below: [] };

    // Build a map of accountId → count of instruments where they appear
    const countMap = new Map<string, number>();
    const summaryMap = new Map<string, { above: RivalSummary[]; below: RivalSummary[] }>();
    for (const entry of loaded) {
      const seen = new Set<string>();
      for (const rival of [...entry.data!.above, ...entry.data!.below]) {
        if (seen.has(rival.accountId)) continue;
        seen.add(rival.accountId);
        countMap.set(rival.accountId, (countMap.get(rival.accountId) ?? 0) + 1);
        if (!summaryMap.has(rival.accountId)) summaryMap.set(rival.accountId, { above: [], below: [] });
        const bucket = summaryMap.get(rival.accountId)!;
        if (entry.data!.above.some(r => r.accountId === rival.accountId)) bucket.above.push(rival);
        else bucket.below.push(rival);
      }
    }

    // Keep only rivals present in ALL loaded instruments
    const threshold = loaded.length;
    const above: RivalSummary[] = [];
    const below: RivalSummary[] = [];
    for (const [accountId, count] of countMap) {
      if (count < threshold) continue;
      const bucket = summaryMap.get(accountId)!;
      // Determine direction: majority vote across instruments
      const dir = bucket.above.length >= bucket.below.length ? 'above' : 'below';
      // Pick the best summary (highest sharedSongCount) for display
      const allEntries = [...bucket.above, ...bucket.below];
      const best = allEntries.reduce((a, b) => a.sharedSongCount >= b.sharedSongCount ? a : b);
      (dir === 'above' ? above : below).push(best);
    }

    // Sort each group by rivalScore descending (most competitive first)
    above.sort((a, b) => b.rivalScore - a.rivalScore);
    below.sort((a, b) => b.rivalScore - a.rivalScore);
    return { above, below };
  }, [instrumentRivals]);
  /* v8 ignore stop */

  const { phase, shouldStagger } = usePageTransition(`rivals:${accountId}`, allReady, hasCachedData);
  const { forDelay: stagger, next: nextStagger, clearAnim } = useStagger(shouldStagger);
  const shared = useRivalsSharedStyles();
  const styles = useMemo(() => ({
    ...shared,
  }), [shared]);

  /* v8 ignore start -- guard + computed state */
  if (!accountId) {
    return <div style={styles.center}>{t('rivals.noPlayer')}</div>;
  }
  /* v8 ignore stop */

  /* v8 ignore start -- render-time helpers */
  /** Compute CSS variable for min name width based on longest name in a rival list. */
  const nameWidthVar = (rivals: RivalSummary[]): React.CSSProperties => {
    const maxLen = rivals.reduce((max, r) => Math.max(max, (r.displayName ?? 'Unknown Player').length), 0);
    return { '--rival-name-width': `${Math.ceil(maxLen * 0.85)}ch` } as React.CSSProperties;
  };

  const navigateToRival = (rivalId: string, rivalName?: string | null) => {
    navigate(Routes.rivalDetail(rivalId, rivalName ?? undefined), { state: { combo, rivalName } });
  };
  /* v8 ignore stop */

  const PREVIEW_COUNT = 3;

  /* v8 ignore start -- computed render state */
  const hasAnyRivals = instrumentRivals.some(r => r.data && (r.data.above.length > 0 || r.data.below.length > 0))
    || (comboRivals && (comboRivals.above.length > 0 || comboRivals.below.length > 0))
    || (commonRivals.above.length > 0 || commonRivals.below.length > 0);
  /* v8 ignore stop */

  /* v8 ignore start -- JSX render tree */
  const firstRunGateCtx = useMemo(() => ({ hasPlayer: true }), []);

  return (
    <Page
      scrollRestoreKey={`rivals:${accountId}`}
      scrollDeps={[phase]}
      loadPhase={phase}
      containerStyle={styles.container}
      before={
        isMobile ? undefined : (
          <PageHeader
            title={activeTab === 'song' ? t('rivals.tabSong') : t('rivals.tabLeaderboard')}
            actions={phase === LoadPhase.ContentIn ? (
              <ActionPill
                icon={activeTab === 'song' ? <IoTrophy size={Size.iconAction} /> : <IoMusicalNotes size={Size.iconAction} />}
                label={activeTab === 'song' ? t('rivals.tabLeaderboard') : t('rivals.tabSong')}
                onClick={toggleTab}
              />
            ) : undefined}
          />
        )
      }
      firstRun={{ key: 'rivals', label: t('rivals.title'), slides: rivalsSlides, gateContext: firstRunGateCtx }}
      fabSpacer={phase === LoadPhase.ContentIn && !hasAnyRivals ? 'none' : 'end'}
    >
      {phase === LoadPhase.ContentIn && (
            <>
              {activeTab === 'song' ? (
            <div style={{ ...flexColumn, gap: Gap.section }}>
              {!hasAnyRivals && (
                <EmptyState fullPage title={t('rivals.noRivals')} style={stagger(200)} onAnimationEnd={clearAnim} />
              )}

              {/* Common rivals (appears in ALL selected instruments, 2+ required) */}
              {(commonRivals.above.length > 0 || commonRivals.below.length > 0) && (
                <div style={styles.section}>
                  <div
                    className={fx.sectionHeaderClickable}
                    style={{ ...styles.sectionHeaderClickable, ...nextStagger() }}
                    onAnimationEnd={clearAnim}
                    onClick={() => navigate(Routes.allRivals('common'), { state: { from: 'rivals' } })}
                    role="button"
                    tabIndex={0}
                    onKeyDown={e => { if (e.key === 'Enter') navigate(Routes.allRivals('common'), { state: { from: 'rivals' } }); }}
                  >
                    <div style={styles.cardHeaderText}>
                      <span style={styles.cardTitle}>{t('rivals.commonRivalsShort', 'Common Rivals')}</span>
                    </div>
                    <span style={styles.seeAll}>{t('rivals.seeAll', 'See All')}</span>
                    <IoChevronForward size={20} style={styles.chevron} />
                  </div>
                  <div style={{ ...styles.rivalList, ...nameWidthVar([...commonRivals.above, ...commonRivals.below]) }}>
                    {[...commonRivals.above, ...commonRivals.below].map(rival => (
                      <RivalRow
                        key={rival.accountId}
                        rival={rival}
                        direction={commonRivals.above.includes(rival) ? 'above' : 'below'}
                        onClick={() => navigateToRival(rival.accountId, rival.displayName)}
                        style={nextStagger()}
                        onAnimationEnd={clearAnim}
                      />
                    ))}
                  </div>
                </div>
              )}

              {/* Combo section (if 2+ instruments enabled) */}
              {combo && comboRivals && (comboRivals.above.length > 0 || comboRivals.below.length > 0) && (
                <div style={styles.section}>
                  <div style={{ ...styles.sectionHeader, ...nextStagger() }} onAnimationEnd={clearAnim}>
                    <div>
                      <span style={styles.cardTitle}>{t('rivals.instrumentRivalsShort', { instrument: t('rivals.combo') })}</span>
                    </div>
                  </div>
                  <div style={{ ...styles.rivalList, ...nameWidthVar([...comboRivals.above, ...comboRivals.below]) }}>
                    {[...comboRivals.above, ...comboRivals.below].map(rival => (
                      <RivalRow
                        key={rival.accountId}
                        rival={rival}
                        direction={comboRivals.above.some(r => r.accountId === rival.accountId) ? 'above' : 'below'}
                        onClick={() => navigateToRival(rival.accountId, rival.displayName)}
                        style={nextStagger()}
                        onAnimationEnd={clearAnim}
                      />
                    ))}
                  </div>
                </div>
              )}

              {/* Per-instrument sections */}
              {instrumentRivals.map(entry => {
                if (!entry.data || (entry.data.above.length === 0 && entry.data.below.length === 0)) return null;
                const previewAbove = entry.data.above.slice(0, PREVIEW_COUNT);
                const previewBelow = entry.data.below.slice(0, PREVIEW_COUNT);
                const allPreview = [...previewAbove, ...previewBelow];
                const navigateToInstrument = () => navigate(Routes.allRivals(entry.instrument), { state: { from: 'rivals' } });
                return (
                  <div key={entry.instrument} style={styles.section}>
                    <div
                      className={fx.sectionHeaderClickable}
                      style={{ ...styles.sectionHeaderClickable, ...nextStagger() }}
                      onAnimationEnd={clearAnim}
                      onClick={navigateToInstrument}
                      role="button"
                      tabIndex={0}
                      onKeyDown={e => { if (e.key === 'Enter') navigateToInstrument(); }}
                    >
                      <InstrumentHeader instrument={entry.instrument} size={InstrumentHeaderSize.SM} iconOnly />
                      <div style={styles.cardHeaderText}>
                        <span style={styles.cardTitle}>{t('rivals.instrumentRivalsShort', { instrument: serverInstrumentLabel(entry.instrument) })}</span>
                      </div>
                      <span style={styles.seeAll}>{t('rivals.seeAll', 'See All')}</span>
                      <IoChevronForward size={20} style={styles.chevron} />
                    </div>
                    <div style={{ ...styles.rivalList, ...nameWidthVar(allPreview) }}>
                      {allPreview.map(rival => (
                        <RivalRow
                          key={rival.accountId}
                          rival={rival}
                          direction={previewAbove.includes(rival) ? 'above' : 'below'}
                          onClick={() => navigateToRival(rival.accountId, rival.displayName)}
                          style={nextStagger()}
                          onAnimationEnd={clearAnim}
                        />
                      ))}
                    </div>
                  </div>
                );
              })}
            </div>
              ) : (
                <LeaderboardRivalsTab accountId={accountId} shouldStagger={shouldStagger} />
              )}
            </>
      )}
    </Page>
  );
}

/* v8 ignore stop */
