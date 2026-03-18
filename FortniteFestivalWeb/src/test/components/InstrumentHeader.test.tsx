import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import React from 'react';
import InstrumentHeader from '../../components/display/InstrumentHeader';
import { InstrumentHeaderSize } from '@festival/core';


describe('InstrumentHeader', () => {
  it('renders icon and label at MD size', () => {
    const { container } = render(
      React.createElement(InstrumentHeader, {
        instrument: 'Solo_Guitar',
        size: InstrumentHeaderSize.MD,
      }),
    );
    expect(container.querySelector('img')).not.toBeNull();
    expect(container.textContent).toBeTruthy();
  });

  it('renders at each size without error', () => {
    for (const size of Object.values(InstrumentHeaderSize)) {
      const { container } = render(
        React.createElement(InstrumentHeader, {
          instrument: 'Solo_Bass',
          size,
        }),
      );
      expect(container.querySelector('img')).not.toBeNull();
    }
  });

  it('renders custom label', () => {
    const { container } = render(
      React.createElement(InstrumentHeader, {
        instrument: 'Solo_Guitar',
        size: InstrumentHeaderSize.SM,
        label: 'Custom Label',
      }),
    );
    expect(container.textContent).toContain('Custom Label');
  });

  it('hides label when iconOnly is true', () => {
    const { container } = render(
      React.createElement(InstrumentHeader, {
        instrument: 'Solo_Guitar',
        size: InstrumentHeaderSize.MD,
        iconOnly: true,
      }),
    );
    expect(container.querySelector('span')).toBeNull();
    expect(container.querySelector('img')).not.toBeNull();
  });

  it('applies custom className', () => {
    const { container } = render(
      React.createElement(InstrumentHeader, {
        instrument: 'Solo_Guitar',
        size: InstrumentHeaderSize.SM,
        className: 'custom-class',
      }),
    );
    expect(container.firstElementChild?.classList.contains('custom-class')).toBe(true);
  });

  it('applies custom style', () => {
    const { container } = render(
      React.createElement(InstrumentHeader, {
        instrument: 'Solo_Guitar',
        size: InstrumentHeaderSize.SM,
        style: { opacity: 0.5 },
      }),
    );
    expect(container.firstElementChild?.getAttribute('style')).toContain('opacity');
  });

  it('renders different instrument types', () => {
    for (const inst of ['Solo_Drums', 'Solo_Vocals', 'Solo_PeripheralGuitar'] as const) {
      const { container } = render(
        React.createElement(InstrumentHeader, {
          instrument: inst,
          size: InstrumentHeaderSize.LG,
        }),
      );
      expect(container.querySelector('img')).not.toBeNull();
    }
  });
});
