import { describe, it, expect, vi } from 'vitest';
import { render } from '@testing-library/react';

vi.mock('../../pages/Page.module.css', () => ({
  default: { spinnerOverlay: 'spinnerOverlay' },
}));

import SuspenseFallback from '../../components/common/SuspenseFallback';

describe('SuspenseFallback', () => {
  it('renders spinner overlay', () => {
    const { container } = render(<SuspenseFallback />);
    expect(container.querySelector('.spinnerOverlay')).toBeTruthy();
  });
});
