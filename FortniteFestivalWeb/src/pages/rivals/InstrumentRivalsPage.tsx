/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
import { useEffect, useState, useCallback, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams, useNavigate } from 'react-router-dom';
import { api } from '../../api/client';
import { useScrollMask } from '../../hooks/ui/useScrollMask';
import { useStaggerRush } from '../../hooks/ui/useStaggerRush';
import { useLoadPhase } from '../../hooks/data/useLoadPhase';
import { useIsMobile } from '../../hooks/ui/useIsMobile';
import { useTrackedPlayer } from '../../hooks/data/useTrackedPlayer';
import ArcSpinner from '../../components/common/ArcSpinner';
import InstrumentHeader from '../../components/display/InstrumentHeader';
import { InstrumentHeaderSize } from '@festival/core';
import { serverInstrumentLabel, type RivalsListResponse, type ServerInstrumentKey } from '@festival/core/api/serverTypes';
import RivalRow from './components/RivalRow';
import { Routes } from '../../routes';
import s from './RivalsPage.module.css';

const VALID_INSTRUMENTS = new Set<string>([
  'Solo_Guitar', 'Solo_Bass', 'Solo_Drums', 'Solo_Vocals',
  'Solo_PeripheralGuitar', 'Solo_PeripheralBass',
]);

/* v8 ignore start -- page component with multiple context/hook dependencies */
export default function InstrumentRivalsPage() {
  const { t } = useTranslation();
  const { accountId, instrument } = useParams<{ accountId: string; instrument: string }>();
  const navigate = useNavigate();
  const isMobile = useIsMobile();
  const { player } = useTrackedPlayer();
  const scrollRef = useRef<HTMLDivElement>(null);

  const [data, setData] = useState<RivalsListResponse | null>(null);
  const [loading, setLoading] = useState(true);

  const validInstrument = instrument && VALID_INSTRUMENTS.has(instrument)
    ? (instrument as ServerInstrumentKey)
    : null;

  useEffect(() => {
    if (!accountId || !validInstrument) return;
    let cancelled = false;
    setLoading(true);
    api.getRivalsList(accountId, validInstrument).then(res => {
      if (!cancelled) { setData(res); setLoading(false); }
    }).catch(() => {
      if (!cancelled) { setData(null); setLoading(false); }
    });
    return () => { cancelled = true; };
  }, [accountId, validInstrument]);

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

  if (!accountId || !validInstrument) {
    return <div className={s.center}>{t('rivals.noPlayer')}</div>;
  }

  const stagger = (delayMs: number): React.CSSProperties => ({
    opacity: 0,
    animation: `fadeInUp 400ms ease-out ${delayMs}ms forwards`,
  });

  const navigateToRival = (rivalId: string) => {
    navigate(Routes.rivalDetail(accountId, rivalId), { state: { combo: validInstrument } });
  };

  const friendlyName = serverInstrumentLabel(validInstrument);
  const playerName = player?.displayName ?? 'Your';
  const hasRivals = data && (data.above.length > 0 || data.below.length > 0);

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
                {t('rivals.instrumentRivals', { player: playerName, instrument: friendlyName })}
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
                          {t('rivals.instrumentRivals', { player: playerName, instrument: friendlyName })}
                        </span>
                        <span className={s.cardDesc}>
                          {t('rivals.ahead', { count: data!.above.length })} / {t('rivals.behind', { count: data!.below.length })}
                        </span>
                      </div>
                      <InstrumentHeader instrument={validInstrument} size={InstrumentHeaderSize.SM} iconOnly />
                    </div>
                  </div>
                  {data!.above.length > 0 && (
                    <>
                      <div className={s.directionLabelAbove}>
                        {t('rivals.aboveYou')} ({data!.above.length})
                      </div>
                      <div className={s.rivalList}>
                        {data!.above.map(rival => (
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
                  {data!.below.length > 0 && (
                    <>
                      <div className={s.directionLabelBelow}>
                        {t('rivals.belowYou')} ({data!.below.length})
                      </div>
                      <div className={s.rivalList}>
                        {data!.below.map(rival => (
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
