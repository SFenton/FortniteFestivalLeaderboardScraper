import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import type { ReactNode } from 'react';
import MobileFloatingActionButton from '../../../../src/components/shell/fab/MobileFloatingActionButton';
import { FabVisibilityProvider, useFabVisibility } from '../../../../src/contexts/FabVisibilityContext';

const mobileChrome = vi.hoisted(() => ({ value: true }));

vi.mock('../../../../src/hooks/ui/useIsMobile', () => ({
  useIsMobileChrome: () => mobileChrome.value,
}));

vi.mock('../../../../src/components/shell/fab/FloatingActionButton', () => ({
  default: ({ ready }: { ready?: boolean }) => (
    <div data-testid="inner-fab" data-ready={String(ready)} />
  ),
}));

function SurfaceState() {
  const { hasMobileFabSurface } = useFabVisibility();
  return <output data-testid="surface-state">{String(hasMobileFabSurface)}</output>;
}

function TestShell({ children }: { children?: ReactNode }) {
  return (
    <FabVisibilityProvider mobileFabHidden={false}>
      <SurfaceState />
      {children}
    </FabVisibilityProvider>
  );
}

describe('MobileFloatingActionButton surface registration', () => {
  it('does not register an empty warm-up mount as a rendered surface', () => {
    render(
      <TestShell>
        <MobileFloatingActionButton ready={false} mode="players" onPress={vi.fn()} />
      </TestShell>,
    );

    expect(screen.getByTestId('inner-fab')).toBeDefined();
    expect(screen.getByTestId('surface-state')).toHaveTextContent('false');
  });

  it('registers only while content can render', () => {
    const action = { label: 'Filter', icon: <span>F</span>, onPress: vi.fn() };
    const { rerender } = render(
      <TestShell>
        <MobileFloatingActionButton ready mode="players" actionGroups={[[action]]} onPress={vi.fn()} />
      </TestShell>,
    );

    expect(screen.getByTestId('surface-state')).toHaveTextContent('true');

    rerender(
      <TestShell>
        <MobileFloatingActionButton ready mode="players" onPress={vi.fn()} />
      </TestShell>,
    );
    expect(screen.getByTestId('surface-state')).toHaveTextContent('false');
  });

  it('keeps the surface registered until every FAB unregisters', () => {
    const { rerender } = render(
      <TestShell>
        <MobileFloatingActionButton key="first" ready mode="players" directAction onPress={vi.fn()} />
        <MobileFloatingActionButton key="second" ready mode="players" directAction onPress={vi.fn()} />
      </TestShell>,
    );
    expect(screen.getByTestId('surface-state')).toHaveTextContent('true');

    rerender(
      <TestShell>
        <MobileFloatingActionButton key="second" ready mode="players" directAction onPress={vi.fn()} />
      </TestShell>,
    );
    expect(screen.getByTestId('surface-state')).toHaveTextContent('true');

    rerender(<TestShell />);
    expect(screen.getByTestId('surface-state')).toHaveTextContent('false');
  });
});
