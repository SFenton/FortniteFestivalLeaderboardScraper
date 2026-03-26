/**
 * First-run demo: General suggestion type toggles.
 * Renders SUGGESTION_TYPES as interactive ToggleRow components.
 */
import { useState, useCallback } from 'react';
import { ToggleRow } from '../../../../components/common/ToggleRow';
import { SUGGESTION_TYPES } from '@festival/core/suggestions/suggestionFilterConfig';
import { Layout } from '@festival/theme';
import FadeIn from '../../../../components/page/FadeIn';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';

export default function GlobalFilterDemo() {
  const h = useSlideHeight();
  const maxToggles = h
    ? Math.max(1, Math.floor(h / Layout.filterToggleRowHeight))
    : SUGGESTION_TYPES.length;

  const [toggleState, setToggleState] = useState<Record<string, boolean>>(() => {
    const s: Record<string, boolean> = {};
    for (const st of SUGGESTION_TYPES) { s[st.id] = true; }
    return s;
  });

  const toggle = useCallback((id: string) => {
    setToggleState(prev => ({ ...prev, [id]: !prev[id] }));
  }, []);

  const visible = SUGGESTION_TYPES.slice(0, maxToggles);

  return (
    <FadeIn delay={0} style={{ width: '100%' }}>
      {visible.map(st => (
        <ToggleRow
          key={st.id}
          label={st.label}
          description={st.description}
          checked={!!toggleState[st.id]}
          onToggle={() => toggle(st.id)}
        />
      ))}
    </FadeIn>
  );
}
