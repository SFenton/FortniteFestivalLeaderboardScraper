import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import { InstrumentChip } from '../../../src/components/display/InstrumentChip';
import { Colors } from '@festival/theme';

describe('InstrumentChip', () => {
  it('renders with no score and no FC', () => {
    const { container } = render(
      <InstrumentChip instrument="Solo_Guitar" hasScore={false} isFC={false} />,
    );
    const chip = container.firstElementChild as HTMLElement;
    expect(chip.style.backgroundColor).toBeTruthy();
    expect(chip.style.borderColor).toBeTruthy();
  });

  it('renders scored status', () => {
    const { container } = render(
      <InstrumentChip instrument="Solo_Bass" hasScore={true} isFC={false} />,
    );
    const chip = container.firstElementChild as HTMLElement;
    expect(chip.style.backgroundColor).toBeTruthy();
  });

  it('renders FC status (overrides scored)', () => {
    const { container } = render(
      <InstrumentChip instrument="Solo_Drums" hasScore={true} isFC={true} />,
    );
    const chip = container.firstElementChild as HTMLElement;
    expect(chip.style.backgroundColor).toBeTruthy();
  });

  it('renders FC even when hasScore is false', () => {
    const { container } = render(
      <InstrumentChip instrument="Solo_Vocals" hasScore={false} isFC={true} />,
    );
    const chip = container.firstElementChild as HTMLElement;
    expect(chip.style.backgroundColor).toBeTruthy();
  });

  it('renders with default size', () => {
    const { container } = render(
      <InstrumentChip instrument="Solo_Guitar" hasScore={false} isFC={false} />,
    );
    const img = container.querySelector('img');
    expect(img?.getAttribute('width')).toBe('24');
  });

  it('renders with custom size', () => {
    const { container } = render(
      <InstrumentChip instrument="Solo_Guitar" hasScore={false} isFC={false} size={32} />,
    );
    const img = container.querySelector('img');
    expect(img?.getAttribute('width')).toBe('32');
  });

  it('renders all server instrument keys', () => {
    const keys = ['Solo_Guitar', 'Solo_Bass', 'Solo_Drums', 'Solo_Vocals', 'Solo_PeripheralGuitar', 'Solo_PeripheralBass'] as const;
    for (const key of keys) {
      const { container } = render(
        <InstrumentChip instrument={key} hasScore={false} isFC={false} />,
      );
      expect(container.querySelector('img')).toBeTruthy();
    }
  });
});
