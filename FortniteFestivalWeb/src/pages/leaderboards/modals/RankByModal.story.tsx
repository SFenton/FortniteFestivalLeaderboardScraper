import { useState } from 'react';
import RankByModal from './RankByModal';
import type { RankingMetric } from '@festival/core/api';

export function MetricHelpFocusLifecycle() {
  const [visible, setVisible] = useState(false);
  const [draft, setDraft] = useState<RankingMetric>('fcrate');
  return (
    <div>
      <button type="button" onClick={() => setVisible(true)}>Open Rank By</button>
      <button type="button">Background action</button>
      <RankByModal
        visible={visible}
        draft={draft}
        onDraftChange={setDraft}
        onClose={() => setVisible(false)}
        onApply={() => setVisible(false)}
        onReset={() => setDraft('totalscore')}
        experimentalRanksEnabled
        metrics={['adjusted', 'fcrate']}
      />
    </div>
  );
}
