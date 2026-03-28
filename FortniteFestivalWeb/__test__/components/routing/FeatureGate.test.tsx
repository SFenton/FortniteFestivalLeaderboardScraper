import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter, Routes, Route } from 'react-router-dom';

// Mock feature flags
const mockFlags = vi.hoisted(() => ({
  shop: true, rivals: true, compete: true, leaderboards: true, firstRun: true,
}));

vi.mock('../../../src/contexts/FeatureFlagsContext', () => ({
  useFeatureFlags: () => mockFlags,
}));

import FeatureGate from '../../../src/components/routing/FeatureGate';

function renderWithRoute(flag: 'shop' | 'rivals' | 'compete' | 'leaderboards', initialPath = '/test') {
  return render(
    <MemoryRouter initialEntries={[initialPath]}>
      <Routes>
        <Route path="/test" element={
          <FeatureGate flag={flag}><div data-testid="child">Protected</div></FeatureGate>
        } />
        <Route path="/songs" element={<div data-testid="redirected">Redirected</div>} />
      </Routes>
    </MemoryRouter>,
  );
}

describe('FeatureGate', () => {
  it('renders children when flag is on', () => {
    mockFlags.shop = true;
    renderWithRoute('shop');
    expect(screen.getByTestId('child')).toBeTruthy();
  });

  it('redirects to /songs when flag is off', () => {
    mockFlags.shop = false;
    renderWithRoute('shop');
    expect(screen.queryByTestId('child')).toBeNull();
    expect(screen.getByTestId('redirected')).toBeTruthy();
  });

  it('works with rivals flag', () => {
    mockFlags.rivals = false;
    renderWithRoute('rivals');
    expect(screen.getByTestId('redirected')).toBeTruthy();
  });

  it('works with compete flag', () => {
    mockFlags.compete = false;
    renderWithRoute('compete');
    expect(screen.getByTestId('redirected')).toBeTruthy();
  });

  it('works with leaderboards flag', () => {
    mockFlags.leaderboards = false;
    renderWithRoute('leaderboards');
    expect(screen.getByTestId('redirected')).toBeTruthy();
  });
});
