import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import Modal from '../../../components/modals/Modal';
import { ModalSection } from '../../../components/modals/components/ModalSection';
import { RadioRow } from '../../../components/common/RadioRow';
import FirstRunCarousel from '../../../components/firstRun/FirstRunCarousel';
import { getEnabledRankingMetrics, isExperimentalRankingMetric } from '../helpers/rankingHelpers';
import { getMetricInfoSlides } from '../firstRun/metricInfo';
import type { RankingMetric } from '@festival/core/api/serverTypes';

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
};

export default function RankByModal({ visible, draft, onDraftChange, onClose, onApply, onReset, experimentalRanksEnabled, metrics, subject = 'players' }: RankByModalProps) {
  const { t } = useTranslation();
  const [infoMetric, setInfoMetric] = useState<RankingMetric | null>(null);
  const metricOptions = metrics ?? getEnabledRankingMetrics(experimentalRanksEnabled);
  const usesBandCopy = subject === 'bands';

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
          {metricOptions.map((m) => (
            <RadioRow
              key={m}
              label={t(`rankings.metric.${m}`)}
              hint={t(usesBandCopy ? `rankings.bandMetric.${m}Desc` : `rankings.metric.${m}Desc`)}
              selected={draft === m}
              onSelect={() => onDraftChange(m)}
              onInfo={!usesBandCopy && isExperimentalRankingMetric(m) ? () => setInfoMetric(m) : undefined}
            />
          ))}
        </ModalSection>
      </Modal>
      {infoMetric && <FirstRunCarousel slides={getMetricInfoSlides(infoMetric)} onDismiss={() => {}} onExitComplete={() => setInfoMetric(null)} />}
    </>
  );
}
