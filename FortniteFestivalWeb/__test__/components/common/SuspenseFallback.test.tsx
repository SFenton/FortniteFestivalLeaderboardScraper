import { describe, it, expect, vi } from 'vitest';
import { render } from '@testing-library/react';

import SuspenseFallback from '../../../src/components/common/SuspenseFallback';

describe('SuspenseFallback', () => {
  it('renders spinner overlay', () => {
    const { container } = render(<SuspenseFallback />);
    // pageCss.spinnerOverlay applies inline styles (position: fixed, etc.)
    const overlay = container.firstElementChild as HTMLElement;
    expect(overlay).toBeTruthy();
    expect(overlay.style.position).toBe('fixed');
  });
});
