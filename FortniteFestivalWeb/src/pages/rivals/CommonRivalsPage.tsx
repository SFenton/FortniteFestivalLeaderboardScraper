/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useEffect, useState, useCallback, useRef, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams, useNavigate } from 'react-router-dom';
import { api } from '../../api/client';
import { useSettings, visibleInstruments } from '../../contexts/SettingsContext';
import { useScrollMask } from '../../hooks/ui/useScrollMask';
import { useStaggerRush } from '../../hooks/ui/useStaggerRush';
import { useLoadPhase } from '../../hooks/data/useLoadPhase';
import { useIsMobile } from '../../hooks/ui/useIsMobile';
import { useTrackedPlayer } from '../../hooks/data/useTrackedPlayer';
import ArcSpinner from '../../components/common/ArcSpinner';
import type { RivalsListResponse, RivalSummary, ServerInstrumentKey } from '@festival/core/api/serverTypes';
import RivalRow from './components/RivalRow';
import { Routes } from '../../routes';
import { deriveComboFromSettings } from './helpers/comboUtils';
import s from './RivalsPage.module.css';

/* v8 ignore start -- page component with multiple context/hook dependencies */
export default function CommonRivalsPage() {
  const { t } = useTranslation();
  const { accountId } = useParams<{ accountId: string }>();
  const navigate = useNavigate();
  const { settings } = useSettings();
  const isMobile = useIsMobile();
  const { player } = useTrackedPlayer();
  const scrollRef = useRef<HTMLDivElement>(null);
  const combo = useMemo(() => deriveComboFromSettings(settings), [settings]);

  const activeInstruments = visibleInstruments(settings);

  const [instrumentData, setInstrumentData] = useState<Map<ServerInstrumentKey, RivalsListResponse>>(new Map());
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    if (!accountId || activeInstruments.length < 2) {
      setLoading(false);
      return;
    }
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
  }, [accountId, settings.showLead, settings.showBass, settings.showDrums, settings.showVocals, settings.showProLead, settings.showProBass]);

  const commonRivals = useMemo<{ above: RivalSummary[]; below: RivalSummary[] }>(() => {
    if (instrumentData.size < 2) return { above: [], below: [] };

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
  }, [instrumentData]);

  const { phase } = useLoadPhase(!loading);
  const updateScrollMask = useScrollMask(scrollRef, [phase]);
  const { rushOnScroll } = useStaggerRush(scrollRef);

  const handleScroll = useCallback(() => {
    updateScrollMask();
    rushOnScroll();
  }, [updateScrollMask, rushOnScroll]);

  const clearAnim = useCallback((e: React.AnimationEvent<HTMLElement>) => {
    const el = e.currentTarget;
    el.style.opacity = '';
    el.style.animation = '';
  }, []);

  if (!accountId) {
    return <div className={s.center}>{t('rivals.noPlayer')}</div>;
  }

  const stagger = (delayMs: number): React.CSSProperties => ({
    opacity: 0,
    animation: `fadeInUp 400ms ease-out ${delayMs}ms forwards`,
  });

  const navigateToRival = (rivalId: string) => {
    navigate(Routes.rivalDetail(accountId, rivalId), { state: { combo } });
  };

  const playerName = player?.displayName ?? 'Your';
  const hasRivals = commonRivals.above.length > 0 || commonRivals.below.length > 0;

  return (
    <div className={s.page}>
      {phase !== 'contentIn' && (
        <div
          className={s.spinnerOverlay}
          style={phase === 'spinnerOut' ? { animation: 'fadeOut 500ms ease-out forwards' } : undefined}
        >
          <ArcSpinner />
        </div>
      )}
      {phase === 'contentIn' && (
        <>
          <div className={s.stickyHeader}>
            <div className={s.headerContent} style={stagger(100)} onAnimationEnd={clearAnim}>
              <div className={s.headerTitle}>
                {t('rivals.commonRivals', { player: playerName })}
              </div>
            </div>
          </div>
          <div ref={scrollRef} onScroll={handleScroll} className={s.scrollArea}>
            <div className={s.container} style={isMobile ? { paddingBottom: 96 } : undefined}>
              {!hasRivals && (
                <div className={s.emptyState} style={stagger(200)} onAnimationEnd={clearAnim}>
                  <div className={s.emptyTitle}>{t('rivals.noRivals')}</div>
                </div>
              )}

              {hasRivals && (
                <div className={s.card} style={stagger(200)} onAnimationEnd={clearAnim}>
                  <div className={s.cardHeader}>
                    <div className={s.cardHeaderRow}>
                      <div>
                        <span className={s.cardTitle}>
                          {t('rivals.commonRivals', { player: playerName })}
                        </span>
                        <span className={s.cardDesc}>
                          {t('rivals.ahead', { count: commonRivals.above.length })} / {t('rivals.behind', { count: commonRivals.below.length })}
                        </span>
                      </div>
                    </div>
                  </div>
                  {commonRivals.above.length > 0 && (
                    <>
                      <div className={s.directionLabelAbove}>
                        {t('rivals.aboveYou')} ({commonRivals.above.length})
                      </div>
                      <div className={s.rivalList}>
                        {commonRivals.above.map(rival => (
                          <RivalRow
                            key={rival.accountId}
                            rival={rival}
                            direction="above"
                            onClick={() => navigateToRival(rival.accountId)}
                          />
                        ))}
                      </div>
                    </>
                  )}
                  {commonRivals.below.length > 0 && (
                    <>
                      <div className={s.directionLabelBelow}>
                        {t('rivals.belowYou')} ({commonRivals.below.length})
                      </div>
                      <div className={s.rivalList}>
                        {commonRivals.below.map(rival => (
                          <RivalRow
                            key={rival.accountId}
                            rival={rival}
                            direction="below"
                            onClick={() => navigateToRival(rival.accountId)}
                          />
                        ))}
                      </div>
                    </>
                  )}
                </div>
              )}
            </div>
          </div>
        </>
      )}
    </div>
  );
}
/* v8 ignore stop */
