import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { InstrumentHeaderSize } from '@festival/core';
import { STAGGER_INTERVAL } from '@festival/theme';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import InstrumentHeader from '../../../../components/display/InstrumentHeader';
import { LeaderboardEntry } from '../../../leaderboard/global/components/LeaderboardEntry';
import FadeIn from '../../../../components/page/FadeIn';
import css from './TopScoresDemo.module.css';

const ENTRY_HEIGHT = 44;
const LABEL_HEIGHT = 32;
const BTN_HEIGHT = 44;
const GAP = 8;

type DemoEntry = { rank: number; name: string; score: number; accuracy: number; isFC: boolean; stars: number };

const ENTRIES: DemoEntry[] = [
  { rank: 1, name: 'AceSolo', score: 486500, accuracy: 1000000, isFC: true, stars: 6 },
  { rank: 2, name: 'RiffMaster', score: 412300, accuracy: 980000, isFC: false, stars: 5 },
  { rank: 3, name: 'ChordKing', score: 347100, accuracy: 970000, isFC: false, stars: 5 },
  { rank: 4, name: 'PickSlayer', score: 289600, accuracy: 960000, isFC: false, stars: 5 },
];

export default function TopScoresDemo() {
  const { t } = useTranslation();
  const h = useSlideHeight();

  const maxEntries = useMemo(() => {
    if (!h) return 4;
    const available = h - LABEL_HEIGHT - BTN_HEIGHT - GAP * 2;
    return Math.max(1, Math.min(4, Math.floor(available / (ENTRY_HEIGHT + GAP))));
  }, [h]);

  return (
    <div className={css.wrapper}>
      <FadeIn delay={0}>
        <div className={css.cardLabel}>
          <InstrumentHeader instrument="Solo_Guitar" size={InstrumentHeaderSize.MD} />
        </div>
      </FadeIn>
      <div className={css.list}>
        {ENTRIES.slice(0, maxEntries).map((entry, i) => (
          <FadeIn key={i} delay={(i + 1) * STAGGER_INTERVAL}>
            <div className={css.entryRow}>
              <LeaderboardEntry
                rank={entry.rank}
                displayName={entry.name}
                score={entry.score}
                accuracy={entry.accuracy}
                isFullCombo={entry.isFC}
                stars={entry.stars}
                showAccuracy
                showStars
                scoreWidth="7ch"
              />
            </div>
          </FadeIn>
        ))}
        <FadeIn delay={(maxEntries + 1) * STAGGER_INTERVAL}>
          <div className={css.pulseWrap}>
            <div className={css.viewAllRow}>
              {t('songDetail.viewFullLeaderboard', 'View full leaderboard')}
            </div>
          </div>
        </FadeIn>
      </div>
    </div>
  );
}
