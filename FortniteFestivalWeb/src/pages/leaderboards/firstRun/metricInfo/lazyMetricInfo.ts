import { lazyWithPreload } from '../../../../utils/lazyWithPreload';

const metricInfoCarousel = lazyWithPreload(() => import('./MetricInfoCarousel'));

export const LazyMetricInfoCarousel = metricInfoCarousel.Component;
export const preloadMetricInfoCarousel = metricInfoCarousel.preload;
export const loadMetricInfoCarousel = metricInfoCarousel.load;
export const isMetricInfoCarouselLoaded = metricInfoCarousel.isLoaded;
