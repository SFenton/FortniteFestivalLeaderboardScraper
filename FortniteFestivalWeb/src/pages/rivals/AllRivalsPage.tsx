/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useEffect, useState, useCallback, useRef, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate, useSearchParams, useNavigationType } from 'react-router-dom';
import { api } from '../../api/client';
import { useSettings, visibleInstruments } from '../../contexts/SettingsContext';
import { useScrollMask } from '../../hooks/ui/useScrollMask';
import { useScrollRestore } from '../../hooks/ui/useScrollRestore';
import { useStaggerRush } from '../../hooks/ui/useStaggerRush';
import { useLoadPhase } from '../../hooks/data/useLoadPhase';
import { useIsMobile } from '../../hooks/ui/useIsMobile';
import { useTrackedPlayer } from '../../hooks/data/useTrackedPlayer';
import ArcSpinner from '../../components/common/ArcSpinner';
import InstrumentHeader from '../../components/display/InstrumentHeader';
import { InstrumentHeaderSize } from '@festival/core';
import { serverInstrumentLabel, type RivalsListResponse, type RivalSummary, type ServerInstrumentKey } from '@festival/core/api/serverTypes';
import RivalRow from './components/RivalRow';
import { Routes } from '../../routes';
import { deriveComboFromSettings } from './helpers/comboUtils';
import s from './RivalsPage.module.css';

let _allRivalsHasRendered = false;

// Module-level data cache so back-navigation has instant data
let _cachedAllRivalsKey: string | null = null;
let _cachedInstrumentData: Map<ServerInstrumentKey, RivalsListResponse> = new Map();
let _cachedSingleData: RivalsListResponse | null = null;

const VALID_INSTRUMENTS = new Set<string>([
  'Solo_Guitar', 'Solo_Bass', 'Solo_Drums', 'Solo_Vocals',
  'Solo_PeripheralGuitar', 'Solo_PeripheralBass',
]);

/* v8 ignore start -- page component with multiple context/hook dependencies */
export default function AllRivalsPage() {
  const { t } = useTranslation();
  const [searchParams] = useSearchParams();
  const category = searchParams.get('category') ?? 'common';
  const navigate = useNavigate();
  const navType = useNavigationType();
  const { settings } = useSettings();
  const isMobile = useIsMobile();
  const { player } = useTrackedPlayer();
  const accountId = player?.accountId;
  const scrollRef = useRef<HTMLDivElement>(null);
  const saveScroll = useScrollRestore(scrollRef, `rivals-all:${accountId}:${category}`, navType);

  const activeInstruments = visibleInstruments(settings);
  const combo = useMemo(() => deriveComboFromSettings(settings), [settings]);

  // Determine mode from category
  const isCommon = category === 'common';
  const isCombo = category === 'combo';
  const isInstrument = VALID_INSTRUMENTS.has(category);
  const instrument = isInstrument ? (category as ServerInstrumentKey) : null;

  // ─── Data state (initialize from cache when returning) ─────

  const cacheKey = `${accountId}:${category}`;
  const hasCachedData = cacheKey === _cachedAllRivalsKey;
  const skipAnimRef = useRef(_allRivalsHasRendered && navType === 'POP' && hasCachedData);
  _allRivalsHasRendered = true;
  const [instrumentData, setInstrumentData] = useState<Map<ServerInstrumentKey, RivalsListResponse>>(hasCachedData ? _cachedInstrumentData : new Map());
  const [singleData, setSingleData] = useState<RivalsListResponse | null>(hasCachedData ? _cachedSingleData : null);
  const [loading, setLoading] = useState(!hasCachedData);

  // ─── Fetch: common (all instruments, intersection) ───────────

  useEffect(() => {
    if (!isCommon || !accountId || activeInstruments.length < 2) {
      if (isCommon) setLoading(false);
      return;
    }
    if (hasCachedData) return;
    let cancelled = false;
    setLoading(true);
    const pending = new Map<ServerInstrumentKey, RivalsListResponse>();
    let completed = 0;

    activeInstruments.forEach(inst => {
      api.getRivalsList(accountId, inst).then(res => {
        if (cancelled) return;
        pending.set(inst, res);
        completed++;
        if (completed === activeInstruments.length) {
          setInstrumentData(new Map(pending));
          setLoading(false);
        }
      }).catch(() => {
        if (cancelled) return;
        completed++;
        if (completed === activeInstruments.length) {
          setInstrumentData(new Map(pending));
          setLoading(false);
        }
      });
    });

    return () => { cancelled = true; };
  // eslint-disable-next-line react-hooks/exhaustive-deps -- activeInstruments derived from settings
  }, [isCommon, accountId, settings.showLead, settings.showBass, settings.showDrums, settings.showVocals, settings.showProLead, settings.showProBass]);

  // ─── Fetch: single instrument ────────────────────────────────

  useEffect(() => {
    if (!isInstrument || !accountId || !instrument) return;
    if (hasCachedData) return;
    let cancelled = false;
    setLoading(true);
    api.getRivalsList(accountId, instrument).then(res => {
      if (!cancelled) { setSingleData(res); setLoading(false); }
    }).catch(() => {
      if (!cancelled) { setSingleData(null); setLoading(false); }
    });
    return () => { cancelled = true; };
  }, [isInstrument, accountId, instrument]);

  // ─── Fetch: combo (derived from settings) ────────────────────

  useEffect(() => {
    if (!isCombo || !accountId || !combo) {
      if (isCombo) setLoading(false);
      return;
    }
    if (hasCachedData) return;
    let cancelled = false;
    setLoading(true);
    api.getRivalsList(accountId, combo).then(res => {
      if (!cancelled) { setSingleData(res); setLoading(false); }
    }).catch(() => {
      if (!cancelled) { setSingleData(null); setLoading(false); }
    });
    return () => { cancelled = true; };
  }, [isCombo, accountId, combo]);

  // ─── Common rivals: intersection logic ───────────────────────

  const commonRivals = useMemo<{ above: RivalSummary[]; below: RivalSummary[] }>(() => {
    if (!isCommon || instrumentData.size < 2) return { above: [], below: [] };

    const countMap = new Map<string, number>();
    const summaryMap = new Map<string, { above: RivalSummary[]; below: RivalSummary[] }>();
    for (const [, data] of instrumentData) {
      const seen = new Set<string>();
      for (const rival of [...data.above, ...data.below]) {
        if (seen.has(rival.accountId)) continue;
        seen.add(rival.accountId);
        countMap.set(rival.accountId, (countMap.get(rival.accountId) ?? 0) + 1);
        if (!summaryMap.has(rival.accountId)) summaryMap.set(rival.accountId, { above: [], below: [] });
        const bucket = summaryMap.get(rival.accountId)!;
        if (data.above.some(r => r.accountId === rival.accountId)) bucket.above.push(rival);
        else bucket.below.push(rival);
      }
    }

    const threshold = instrumentData.size;
    const above: RivalSummary[] = [];
    const below: RivalSummary[] = [];
    for (const [id, count] of countMap) {
      if (count < threshold) continue;
      const bucket = summaryMap.get(id)!;
      const dir = bucket.above.length >= bucket.below.length ? 'above' : 'below';
      const allEntries = [...bucket.above, ...bucket.below];
      const best = allEntries.reduce((a, b) => a.sharedSongCount >= b.sharedSongCount ? a : b);
      (dir === 'above' ? above : below).push(best);
    }

    above.sort((a, b) => b.rivalScore - a.rivalScore);
    below.sort((a, b) => b.rivalScore - a.rivalScore);
    return { above, below };
  }, [isCommon, instrumentData]);

  // ─── Resolved rivals for rendering ───────────────────────────

  const rivals: { above: RivalSummary[]; below: RivalSummary[] } = isCommon
    ? commonRivals
    : singleData
      ? { above: singleData.above, below: singleData.below }
      : { above: [], below: [] };

  // Persist data to module-level cache for instant back-nav
  useEffect(() => {
    if (loading) return;
    _cachedAllRivalsKey = cacheKey;
    _cachedInstrumentData = instrumentData;
    _cachedSingleData = singleData;
  }, [loading, cacheKey, instrumentData, singleData]);

  // ─── UI hooks ────────────────────────────────────────────────

  const { phase, shouldStagger } = useLoadPhase(!loading, { skipAnimation: skipAnimRef.current });
  const updateScrollMask = useScrollMask(scrollRef, [phase]);
  const { rushOnScroll } = useStaggerRush(scrollRef);

  const handleScroll = useCallback(() => {
    saveScroll();
    updateScrollMask();
    rushOnScroll();
  }, [saveScroll, updateScrollMask, rushOnScroll]);

  const clearAnim = useCallback((e: React.AnimationEvent<HTMLElement>) => {
    const el = e.currentTarget;
    el.style.opacity = '';
    el.style.animation = '';
  }, []);

  if (!accountId) {
    return <div className={s.center}>{t('rivals.noPlayer')}</div>;
  }

  const stagger = (delayMs: number): React.CSSProperties | undefined =>
    shouldStagger ? { opacity: 0, animation: `fadeInUp 400ms ease-out ${delayMs}ms forwards` } : undefined;

  /** Compute CSS variable for min name width based on longest name in a rival list. */
  const nameWidthVar = (list: RivalSummary[]): React.CSSProperties => {
    const maxLen = list.reduce((max, r) => Math.max(max, (r.displayName ?? 'Unknown Player').length), 0);
    return { '--rival-name-width': `${Math.ceil(maxLen * 0.85)}ch` } as React.CSSProperties;
  };

  const effectiveCombo = isCommon ? combo : isCombo ? combo : instrument;
  const navigateToRival = (rivalId: string, rivalName?: string | null) => {
    navigate(Routes.rivalDetail(rivalId, rivalName ?? undefined), { state: { combo: effectiveCombo, rivalName } });
  };

  const hasRivals = rivals.above.length > 0 || rivals.below.length > 0;
  const allRivals = [...rivals.above, ...rivals.below];

  // Title: "{icon} {friendly name} Rivals" — no icon for common
  const titleText = isCommon
    ? t('rivals.commonRivalsShort', 'Common Rivals')
    : isInstrument
      ? t('rivals.instrumentRivalsShort', { instrument: serverInstrumentLabel(instrument!) })
      : t('rivals.instrumentRivalsShort', { instrument: t('rivals.combo') });

  const staggerInterval = 125;
  let staggerIdx = 0;
  const nextStagger = (): React.CSSProperties | undefined =>
    shouldStagger ? stagger((++staggerIdx) * staggerInterval) : undefined;

  return (
    <div className={s.page}>
      <div className={s.stickyHeader}>
        <div className={s.headerContent}>
          <div className={s.headerTitle} style={{ display: 'flex', alignItems: 'center', gap: 'var(--gap-sm)' }}>
            {isInstrument && instrument && (
              <InstrumentHeader instrument={instrument} size={InstrumentHeaderSize.SM} iconOnly />
            )}
            {titleText}
          </div>
        </div>
      </div>
      {phase !== 'contentIn' && (
        <div
          className={s.spinnerOverlay}
          style={phase === 'spinnerOut' ? { animation: 'fadeOut 500ms ease-out forwards' } : undefined}
        >
          <ArcSpinner />
        </div>
      )}
      {phase === 'contentIn' && (
          <div ref={scrollRef} onScroll={handleScroll} className={s.scrollArea}>
            <div className={s.container} style={isMobile ? { paddingBottom: 96 } : undefined}>
              {!hasRivals && (
                <div className={s.emptyState} style={stagger(200)} onAnimationEnd={clearAnim}>
                  <div className={s.emptyTitle}>{t('rivals.noRivals')}</div>
                </div>
              )}

              {hasRivals && (
                <div className={s.section} style={{ paddingTop: 'var(--gap-md)' }}>
                  <div className={s.rivalList} style={nameWidthVar(allRivals)}>
                    {allRivals.map(rival => (
                      <RivalRow
                        key={rival.accountId}
                        rival={rival}
                        direction={rivals.above.includes(rival) ? 'above' : 'below'}
                        onClick={() => navigateToRival(rival.accountId, rival.displayName)}
                        style={nextStagger()}
                        onAnimationEnd={clearAnim}
                      />
                    ))}
                  </div>
                </div>
              )}
            </div>
          </div>
      )}
    </div>
  );
}
/* v8 ignore stop */
