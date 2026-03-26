/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useState, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { RadioRow } from '../../../../../components/common/RadioRow';
import { DirectionSelector } from '../../../../../components/common/DirectionSelector';
import FadeIn from '../../../../../components/page/FadeIn';
import { useSlideHeight } from '../../../../../firstRun/SlideHeightContext';
import { Layout, TRANSITION_MS } from '@festival/theme';
import { useDemoStyles } from '../../../../songs/firstRun/demo/FilterDemo';

const MODES = ['date', 'score', 'accuracy', 'season'] as const;
type SortMode = typeof MODES[number];

export default function SortControlsDemo() {
  const { t } = useTranslation();
  const s = useDemoStyles();
  const h = useSlideHeight();
  const [activeMode, setActiveMode] = useState<SortMode>('score');
  const [ascending, setAscending] = useState(false);
  const [maxModes, setMaxModes] = useState(4);
  const [showDirection, setShowDirection] = useState(true);
  const [showHint, setShowHint] = useState(true);

  useEffect(() => {
    if (!h) return;
    let remaining = h - Layout.sortHeaderHeight;
    const dirFits = remaining >= Layout.sortModeRowHeight + Layout.sortDirectionHeight;
    if (dirFits) {
      remaining -= Layout.sortDirectionHeight;
      setShowDirection(true);
    } else {
      setShowDirection(false);
    }
    const rows = Math.max(1, Math.floor(remaining / Layout.sortModeRowHeight));
    setMaxModes(Math.min(rows, MODES.length));
    setShowHint(h >= Layout.sortHeaderHeight + MODES.length * Layout.sortModeRowHeight + Layout.sortDirectionHeight + Layout.sortHintPadding);
  }, [h]);

  return (
    <div style={s.wrapper}>
      <FadeIn delay={0} style={showDirection ? s.modeSection : s.modeSectionCompact}>
        <div style={s.sectionHeader}>{t('sort.mode')}</div>
        {showHint && <div style={s.sectionHint}>{t('sort.modeHint')}</div>}
        {MODES.slice(0, maxModes).map((mode) => (
          <RadioRow key={mode} label={t(`sort.${mode}`)} selected={mode === activeMode} onSelect={() => setActiveMode(mode)} />
        ))}
      </FadeIn>

      {showDirection && (
        <FadeIn delay={TRANSITION_MS}>
          <DirectionSelector
            ascending={ascending}
            onChange={setAscending}
            title={t('sort.direction')}
            hint={ascending ? t('sort.ascendingHintScores') : t('sort.descendingHintScores')}
          />
        </FadeIn>
      )}
    </div>
  );
}
