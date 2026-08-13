import FirstRunCarousel from '../../../../components/firstRun/FirstRunCarousel';
import type { ExperimentalRankingMetric } from '../../helpers/rankingHelpers';
import { getMetricInfoSlides } from './index';

type MetricInfoCarouselProps = {
  metric: ExperimentalRankingMetric;
  ariaLabel: string;
  onClose: () => void;
  returnFocusTarget: HTMLElement | null;
};

export default function MetricInfoCarousel({
  metric,
  ariaLabel,
  onClose,
  returnFocusTarget,
}: MetricInfoCarouselProps) {
  return (
    <FirstRunCarousel
      slides={getMetricInfoSlides(metric)}
      onDismiss={() => {}}
      onExitComplete={onClose}
      ariaLabel={ariaLabel}
      returnFocusTarget={returnFocusTarget}
    />
  );
}
