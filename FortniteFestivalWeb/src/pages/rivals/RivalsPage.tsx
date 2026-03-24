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
import ArcSpinner from '../../components/common/ArcSpinner';
import InstrumentHeader from '../../components/display/InstrumentHeader';
import { InstrumentHeaderSize } from '@festival/core';
import type { RivalsListResponse, ServerInstrumentKey } from '@festival/core/api/serverTypes';
import { deriveComboFromSettings } from './helpers/comboUtils';
import RivalRow from './components/RivalRow';
import { Routes } from '../../routes';
import s from './RivalsPage.module.css';

type InstrumentRivals = {
  instrument: ServerInstrumentKey;
  data: RivalsListResponse | null;
  loading: boolean;
  error: string | null;
};

export default function RivalsPage() {
  const { t } = useTranslation();
  const { accountId } = useParams<{ accountId: string }>();
  const navigate = useNavigate();
  const { settings } = useSettings();
  const isMobile = useIsMobile();
  const scrollRef = useRef<HTMLDivElement>(null);

  const activeInstruments = visibleInstruments(settings);
  const combo = useMemo(() => deriveComboFromSettings(settings), [settings]);

  // Data state: one entry per instrument + optional combo
  const [instrumentRivals, setInstrumentRivals] = useState<InstrumentRivals[]>([]);
  const [comboRivals, setComboRivals] = useState<RivalsListResponse | null>(null);
  const [comboLoading, setComboLoading] = useState(false);
  const [computedAt, setComputedAt] = useState<string | null>(null);

  // Fetch overview for computedAt timestamp
  /* v8 ignore start — async data fetch */
  useEffect(() => {
    if (!accountId) return;
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

  const { phase } = useLoadPhase(allReady);
  const updateScrollMask = useScrollMask(scrollRef, [phase]);
  const { rushOnScroll } = useStaggerRush(scrollRef);

  /* v8 ignore start — scroll handler */
  const handleScroll = useCallback(() => {
    updateScrollMask();
    rushOnScroll();
  }, [updateScrollMask, rushOnScroll]);
  /* v8 ignore stop */

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

  const hasAnyRivals = instrumentRivals.some(r => r.data && (r.data.above.length > 0 || r.data.below.length > 0))
    || (comboRivals && (comboRivals.above.length > 0 || comboRivals.below.length > 0));

  let staggerIdx = 0;

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
              <div className={s.headerTitle}>{t('rivals.title')}</div>
              {computedAt && (
                <div className={s.headerSubtitle}>
                  {t('rivals.lastComputed', { date: new Date(computedAt).toLocaleDateString() })}
                </div>
              )}
            </div>
          </div>
          <div ref={scrollRef} onScroll={handleScroll} className={s.scrollArea}>
            <div className={s.container} style={isMobile ? { paddingBottom: 96 } : undefined}>
              {!hasAnyRivals && (
                <div className={s.emptyState} style={stagger(200)} onAnimationEnd={clearAnim}>
                  <div className={s.emptyTitle}>{t('rivals.noRivals')}</div>
                </div>
              )}

              {/* Combo section (if 2+ instruments enabled) */}
              {combo && comboRivals && (comboRivals.above.length > 0 || comboRivals.below.length > 0) && (
                <div className={s.card} style={stagger(200 + staggerIdx++ * 150)} onAnimationEnd={clearAnim}>
                  <div className={s.cardHeader}>
                    <div className={s.cardHeaderRow}>
                      <div>
                        <span className={s.cardTitle}>{t('rivals.combo')}</span>
                        <span className={s.cardDesc}>{t('rivals.sharedSongs', { count: comboRivals.above.length + comboRivals.below.length })}</span>
                      </div>
                    </div>
                  </div>
                  {renderDirectionGroups(comboRivals, navigateToRival, t)}
                </div>
              )}

              {/* Per-instrument sections */}
              {instrumentRivals.map(entry => {
                if (!entry.data || (entry.data.above.length === 0 && entry.data.below.length === 0)) return null;
                const delay = 200 + staggerIdx++ * 150;
                return (
                  <div key={entry.instrument} className={s.card} style={stagger(delay)} onAnimationEnd={clearAnim}>
                    <div className={s.cardHeader}>
                      <div className={s.cardHeaderRow}>
                        <div>
                          <span className={s.cardTitle}>{t(`instruments.${entry.instrument}`, entry.instrument)}</span>
                          <span className={s.cardDesc}>
                            {t('rivals.ahead', { count: entry.data.above.length })} / {t('rivals.behind', { count: entry.data.below.length })}
                          </span>
                        </div>
                        <InstrumentHeader instrument={entry.instrument} size={InstrumentHeaderSize.SM} iconOnly />
                      </div>
                    </div>
                    {renderDirectionGroups(entry.data, navigateToRival, t)}
                  </div>
                );
              })}
            </div>
          </div>
        </>
      )}
    </div>
  );
}

function renderDirectionGroups(
  data: RivalsListResponse,
  onRivalClick: (rivalId: string) => void,
  t: (key: string, opts?: Record<string, unknown>) => string,
) {
  return (
    <>
      {data.above.length > 0 && (
        <>
          <div className={s.directionLabelAbove}>
            {t('rivals.aboveYou')} ({data.above.length})
          </div>
          <div className={s.rivalList}>
            {data.above.map(rival => (
              <RivalRow
                key={rival.accountId}
                rival={rival}
                direction="above"
                onClick={() => onRivalClick(rival.accountId)}
              />
            ))}
          </div>
        </>
      )}
      {data.below.length > 0 && (
        <>
          <div className={s.directionLabelBelow}>
            {t('rivals.belowYou')} ({data.below.length})
          </div>
          <div className={s.rivalList}>
            {data.below.map(rival => (
              <RivalRow
                key={rival.accountId}
                rival={rival}
                direction="below"
                onClick={() => onRivalClick(rival.accountId)}
              />
            ))}
          </div>
        </>
      )}
    </>
  );
}
