import { useState, type CSSProperties } from 'react';
import { MaxWidth } from '@festival/theme';
import { SuggestionsMixLimit } from './SuggestionsMixLimit';

export function AtLimit() {
  const [startedMixes, setStartedMixes] = useState(0);

  return (
    <div style={styles.frame}>
      <SuggestionsMixLimit onStartNewMix={() => setStartedMixes(count => count + 1)} />
      <output data-testid="started-mixes" style={styles.output}>{startedMixes}</output>
    </div>
  );
}

const styles: Record<string, CSSProperties> = {
  frame: {
    maxWidth: MaxWidth.card,
    margin: '0 auto',
  },
  output: {
    display: 'block',
    textAlign: 'center',
  },
};
