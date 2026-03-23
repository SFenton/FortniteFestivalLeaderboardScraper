import { useState, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { RadioRow } from '../../../../components/common/RadioRow';
import { DirectionSelector } from '../../../../components/common/DirectionSelector';
import FadeIn from '../../../../components/page/FadeIn';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import { usePlayerData } from '../../../../contexts/PlayerDataContext';
import { Layout, TRANSITION_MS } from '@festival/theme';
import s from './FilterDemo.module.css';

export default function SortDemo() {
  const { t } = useTranslation();
  const BASE_MODES = [t('sort.title'), t('sort.artist'), t('sort.year')] as const;
  const PLAYER_MODES = [t('sort.title'), t('sort.artist'), t('sort.year'), t('sort.hasFC')] as const;
  const [mode, setMode] = useState<string>('');
  const [asc, setAsc] = useState(false);
  const [maxModes, setMaxModes] = useState<number>(4);
  const [showHint, setShowHint] = useState(true);
  const [showDirection, setShowDirection] = useState(true);
  const h = useSlideHeight();
  const { playerData } = usePlayerData();
  const MODES = playerData ? PLAYER_MODES : BASE_MODES;

  // Initialize mode to first translated label
  useEffect(() => {
    setMode(prev => prev || MODES[0]!);
  }, [MODES]);

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
  }, [h, MODES.length]);

  return (
    <div className={s.wrapper}>
      <FadeIn delay={0} className={showDirection ? s.modeSection : s.modeSectionCompact}>
        <div className={s.sectionHeader}>{t('sort.mode')}</div>
        {showHint && (
          <div className={s.sectionHint}>
            {t('sort.modeHint')}
          </div>
        )}
        {MODES.slice(0, maxModes).map(m => (
          <RadioRow key={m} label={m} selected={mode === m} onSelect={() => setMode(m)} />
        ))}
      </FadeIn>

      {showDirection && (
        <FadeIn delay={TRANSITION_MS}>
          <DirectionSelector ascending={asc} onChange={setAsc} />
        </FadeIn>
      )}
    </div>
  );
}
