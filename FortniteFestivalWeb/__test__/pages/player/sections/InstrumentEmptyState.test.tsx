import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import type { ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import InstrumentEmptyState from '../../../../src/pages/player/sections/InstrumentEmptyState';

const t = (key: string, opts?: Record<string, unknown>) => {
  if (key === 'player.noScoresYet') return 'No scores yet';
  if (key === 'player.noScoresYetSubtitle') return `Play some songs on ${opts?.instrument} to see your stats appear here.`;
  return key;
};

describe('InstrumentEmptyState', () => {
  it('renders the "No scores yet" title', () => {
    const { getByText } = render(<InstrumentEmptyState instrument={'Solo_PeripheralDrums' as InstrumentKey} t={t} />);
    expect(getByText('No scores yet')).toBeTruthy();
  });

  it('renders the subtitle with the canonical instrument label', () => {
    const cases: Array<[InstrumentKey, string]> = [
      ['Solo_PeripheralVocals', 'Mic Mode'],
      ['Solo_PeripheralDrums', 'Pro Drums'],
      ['Solo_PeripheralCymbals', 'Pro Drums + Cymbals'],
    ];
    for (const [inst, label] of cases) {
      const { container } = render(<InstrumentEmptyState instrument={inst} t={t} />);
      expect(container.textContent).toContain(`Play some songs on ${label}`);
    }
  });

  it('applies a transparent, centered layout', () => {
    const { getByTestId } = render(
      <InstrumentEmptyState instrument={'Solo_PeripheralDrums' as InstrumentKey} t={t} />,
    );
    const el = getByTestId('inst-empty-Solo_PeripheralDrums') as HTMLElement;
    expect(el.style.textAlign).toBe('center');
    // No explicit background — we rely on parent container having none
    expect(el.style.background).toBe('');
  });
});
