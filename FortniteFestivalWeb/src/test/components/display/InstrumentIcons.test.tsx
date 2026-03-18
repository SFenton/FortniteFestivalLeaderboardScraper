import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import { InstrumentIcon, getInstrumentStatusVisual, INSTRUMENT_STATUS_COLORS } from '../../../components/display/InstrumentIcons';


import { Colors, Size } from '@festival/theme';

describe('InstrumentIcon', () => {
  it('renders an img element', () => {
    const { container } = render(<InstrumentIcon instrument={'guitar'} size={Size.iconTab} />);
    const img = container.querySelector('img');
    expect(img).toBeTruthy();
  });

  it('accepts server instrument keys', () => {
    const { container } = render(<InstrumentIcon instrument={'Solo_Guitar'} size={Size.iconTab} />);
    const img = container.querySelector('img');
    expect(img).toBeTruthy();
    expect(img?.src).toContain('guitar');
  });

  it('applies size', () => {
    const { container } = render(<InstrumentIcon instrument={'drums'} size={36} />);
    const img = container.querySelector('img');
    expect(img?.getAttribute('width')).toBe('36');
  });
});

describe('INSTRUMENT_STATUS_COLORS', () => {
  it('has fullCombo, hasScore, noScore entries', () => {
    expect(INSTRUMENT_STATUS_COLORS.fullCombo.fill).toBe(Colors.gold);
    expect(INSTRUMENT_STATUS_COLORS.hasScore.fill).toBe(Colors.statusGreen);
    expect(INSTRUMENT_STATUS_COLORS.noScore.fill).toBe(Colors.statusRed);
  });
});

describe('getInstrumentStatusVisual', () => {
  it('returns fullCombo for FC', () => {
    expect(getInstrumentStatusVisual(true, true)).toBe(INSTRUMENT_STATUS_COLORS.fullCombo);
  });

  it('returns hasScore when has score but no FC', () => {
    expect(getInstrumentStatusVisual(true, false)).toBe(INSTRUMENT_STATUS_COLORS.hasScore);
  });

  it('returns noScore when no score', () => {
    expect(getInstrumentStatusVisual(false, false)).toBe(INSTRUMENT_STATUS_COLORS.noScore);
  });
});
