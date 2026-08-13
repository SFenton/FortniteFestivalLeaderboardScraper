import { useLayoutEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import Modal from '../../../components/modals/Modal';
import { ModalSection } from '../../../components/modals/components/ModalSection';
import { RadioRow } from '../../../components/common/RadioRow';
import LazyModalBoundary from '../../../components/common/LazyModalBoundary';
import {
  LazyMetricInfoCarousel,
  isMetricInfoCarouselLoaded,
  loadMetricInfoCarousel,
  preloadMetricInfoCarousel,
} from '../firstRun/metricInfo/lazyMetricInfo';
import {
  getEnabledRankingMetrics,
  isExperimentalRankingMetric,
  type ExperimentalRankingMetric,
} from '../helpers/rankingHelpers';
import type { RankingMetric } from '@festival/core/api';

type RankByModalProps = {
  visible: boolean;
  draft: RankingMetric;
  onDraftChange: (metric: RankingMetric) => void;
  onClose: () => void;
  onApply: () => void;
  onReset: () => void;
  experimentalRanksEnabled: boolean;
  metrics?: RankingMetric[];
  subject?: 'players' | 'bands';
  playerScope?: 'instrument' | 'combo' | 'family';
};

export default function RankByModal({
  visible,
  draft,
  onDraftChange,
  onClose,
  onApply,
  onReset,
  experimentalRanksEnabled,
  metrics,
  subject = 'players',
  playerScope = 'instrument',
}: RankByModalProps) {
  const { t } = useTranslation();
  const [infoMetric, setInfoMetric] = useState<ExperimentalRankingMetric | null>(null);
  const infoReturnFocusRef = useRef<HTMLElement | null>(null);
  const pendingInfoFocusRef = useRef<HTMLElement | null>(null);
  const metricOptions = metrics ?? getEnabledRankingMetrics(experimentalRanksEnabled);
  const usesBandCopy = subject === 'bands';
  const metricDescriptionGroup = usesBandCopy
    ? 'bandMetric'
    : playerScope === 'instrument'
      ? 'metric'
      : `${playerScope}Metric`;
  const closeMetricInfo = () => {
    pendingInfoFocusRef.current = infoReturnFocusRef.current;
    setInfoMetric(null);
  };
  const openMetricInfo = (metric: ExperimentalRankingMetric) => {
    infoReturnFocusRef.current = document.activeElement instanceof HTMLElement
      ? document.activeElement
      : null;
    preloadMetricInfoCarousel();
    setInfoMetric(metric);
  };
  const infoMetricLabel = infoMetric
    ? t('rankings.metricInfoAriaLabel', { metric: t(`rankings.metric.${infoMetric}`) })
    : t('rankings.rankBy');

  useLayoutEffect(() => {
    if (infoMetric != null) return;
    const target = pendingInfoFocusRef.current;
    pendingInfoFocusRef.current = null;
    if (target?.isConnected) target.focus({ preventScroll: true });
  }, [infoMetric]);

  return (
    <>
      <Modal
        visible={visible}
        title={t('rankings.rankBy')}
        onClose={onClose}
        onApply={onApply}
        onReset={onReset}
        resetLabel={t('rankings.rankByReset')}
        resetHint={t('rankings.rankByResetHint')}
      >
        <ModalSection title={t('rankings.rankBy')} hint={t(usesBandCopy ? 'rankings.rankByBandHint' : 'rankings.rankByHint')}>
          {metricOptions.map((metric) => {
            const hasInfo = !usesBandCopy
              && playerScope === 'instrument'
              && isExperimentalRankingMetric(metric);
            return (
              <RadioRow
                key={metric}
                label={t(`rankings.metric.${metric}`)}
                hint={t(`rankings.${metricDescriptionGroup}.${metric}Desc`)}
                selected={draft === metric}
                onSelect={() => onDraftChange(metric)}
                onInfo={hasInfo ? () => openMetricInfo(metric) : undefined}
                infoLabel={hasInfo ? t('rankings.metricInfoButton', {
                  metric: t(`rankings.metric.${metric}`),
                }) : undefined}
              />
            );
          })}
        </ModalSection>
      </Modal>
      <LazyModalBoundary
        visible={infoMetric != null}
        title={infoMetricLabel}
        boundaryName="rank-metric-info"
        onClose={closeMetricInfo}
        load={loadMetricInfoCarousel}
        isLoaded={isMetricInfoCarouselLoaded}
        initialFocus="panel"
      >
        {infoMetric && (
          <LazyMetricInfoCarousel
            metric={infoMetric}
            ariaLabel={infoMetricLabel}
            onClose={closeMetricInfo}
            returnFocusTarget={infoReturnFocusRef.current}
          />
        )}
      </LazyModalBoundary>
    </>
  );
}
