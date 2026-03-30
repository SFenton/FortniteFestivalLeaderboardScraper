/* eslint-disable react/forbid-dom-props -- useStyles pattern */
/**
 * First-run demo: Modal-style metric selector that auto-cycles through
 * only the experimental ranking metrics (excludes Total Score).
 *
 * Height-responsive: shows hints when there's room, hides them when tight,
 * and trims metrics when very small.
 */
import { useState, useEffect, useRef, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { RadioRow } from '../../../../components/common/RadioRow';
import FadeIn from '../../../../components/page/FadeIn';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import { EXPERIMENTAL_METRICS } from '../../helpers/rankingHelpers';
import { useDemoStyles } from '../../../songs/firstRun/demo/FilterDemo';
import {
  DEMO_SWAP_INTERVAL_MS,
} from '@festival/theme';

/** Approximate height of a RadioRow WITH a hint (label + description + padding). */
const ROW_WITH_HINT = 86;
/** Approximate height of a RadioRow WITHOUT a hint (label only + padding). */
const ROW_NO_HINT = 48;
/** Height of the "Rank By" header + hint line. */
const HEADER_WITH_HINT = 68;
/** Height of the "Rank By" header only. */
const HEADER_NO_HINT = 36;

export default function ExperimentalMetricsDemo() {
  const { t } = useTranslation();
  const h = useSlideHeight();
  const s = useDemoStyles();

  const metrics = EXPERIMENTAL_METRICS;
  const [activeIdx, setActiveIdx] = useState(0);
  const idxRef = useRef(0);

  const rotate = useCallback(() => {
    idxRef.current = (idxRef.current + 1) % metrics.length;
    setActiveIdx(idxRef.current);
  }, [metrics.length]);

  useEffect(() => {
    const timer = setInterval(rotate, DEMO_SWAP_INTERVAL_MS);
    return () => clearInterval(timer);
  }, [rotate]);

  // Determine what fits: try hints first, then without hints, then trim rows
  const budget = h || 320;
  let showHints = true;
  let showHeaderHint = true;
  let maxRows = metrics.length;

  // Try with hints
  const withHintsHeight = HEADER_WITH_HINT + metrics.length * ROW_WITH_HINT;
  if (withHintsHeight <= budget) {
    // Everything fits with hints
  } else {
    // Try without row hints but keep header hint
    const noRowHints = HEADER_WITH_HINT + metrics.length * ROW_NO_HINT;
    if (noRowHints <= budget) {
      showHints = false;
    } else {
      // Drop header hint too
      showHints = false;
      showHeaderHint = false;
      const rowH = ROW_NO_HINT;
      const headerH = HEADER_NO_HINT;
      maxRows = Math.max(1, Math.floor((budget - headerH) / rowH));
    }
  }

  const visibleMetrics = metrics.slice(0, Math.min(maxRows, metrics.length));
  const activeMetric = metrics[activeIdx]!;

  return (
    <div style={s.wrapper}>
      <FadeIn delay={0} style={s.modeSection}>
        <div style={s.sectionHeader}>{t('rankings.rankBy')}</div>
        {showHeaderHint && (
          <div style={s.sectionHint}>{t('rankings.rankByHint')}</div>
        )}
        {visibleMetrics.map(m => (
          <RadioRow
            key={m}
            label={t(`rankings.metric.${m}`)}
            hint={showHints ? t(`rankings.metric.${m}Desc`) : undefined}
            selected={activeMetric === m}
            onSelect={() => {}}
          />
        ))}
      </FadeIn>
    </div>
  );
}
