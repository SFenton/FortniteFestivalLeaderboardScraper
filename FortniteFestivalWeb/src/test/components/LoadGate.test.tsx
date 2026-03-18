import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { LoadPhase } from '@festival/core';

// Mock Page.module.css
vi.mock('../../pages/Page.module.css', () => ({
  default: {
    spinnerOverlay: 'spinnerOverlay',
    spinnerContainer: 'spinnerContainer',
  },
}));

// Must import after mock
import { LoadGate } from '../../components/page/LoadGate';

describe('LoadGate (page)', () => {
  it('shows spinner during Loading phase', () => {
    const { container } = render(
      <LoadGate phase={LoadPhase.Loading}>
        <div data-testid="content">Content</div>
      </LoadGate>,
    );
    expect(container.querySelector('[class*="spinner"]')).toBeTruthy();
    expect(screen.queryByTestId('content')).toBeNull();
  });

  it('shows fading spinner during SpinnerOut phase', () => {
    const { container } = render(
      <LoadGate phase={LoadPhase.SpinnerOut}>
        <div data-testid="content">Content</div>
      </LoadGate>,
    );
    const spinnerDiv = container.querySelector('[class*="spinner"]');
    expect(spinnerDiv).toBeTruthy();
    expect(spinnerDiv?.getAttribute('style')).toContain('fadeOut');
    expect(screen.queryByTestId('content')).toBeNull();
  });

  it('shows content during ContentIn phase', () => {
    const { container } = render(
      <LoadGate phase={LoadPhase.ContentIn}>
        <div data-testid="content">Content</div>
      </LoadGate>,
    );
    expect(container.querySelector('[class*="spinner"]')).toBeNull();
    expect(screen.getByTestId('content')).toBeTruthy();
  });

  it('overlay mode always renders children', () => {
    render(
      <LoadGate phase={LoadPhase.Loading} overlay>
        <div data-testid="content">Content</div>
      </LoadGate>,
    );
    expect(screen.getByTestId('content')).toBeTruthy();
  });

  it('overlay mode hides spinner during ContentIn', () => {
    const { container } = render(
      <LoadGate phase={LoadPhase.ContentIn} overlay>
        <div data-testid="content">Content</div>
      </LoadGate>,
    );
    expect(container.querySelector('[class*="spinner"]')).toBeNull();
    expect(screen.getByTestId('content')).toBeTruthy();
  });

  it('uses custom spinnerClassName', () => {
    const { container } = render(
      <LoadGate phase={LoadPhase.Loading} spinnerClassName="custom-spinner">
        <div>Content</div>
      </LoadGate>,
    );
    expect(container.querySelector('.custom-spinner')).toBeTruthy();
  });
});
