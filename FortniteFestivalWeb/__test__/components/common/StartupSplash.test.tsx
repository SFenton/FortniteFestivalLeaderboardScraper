import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { TRANSITION_MS, ZIndex } from '@festival/theme';
import StartupSplash from '../../../src/components/common/StartupSplash';

describe('StartupSplash', () => {
  it('renders one centered spinner with hidden loading status when announcing', () => {
    render(<StartupSplash announce />);

    const splash = screen.getByRole('status');
    expect(splash).toHaveAttribute('aria-busy', 'true');
    expect(splash).toHaveAttribute('data-phase', 'covered');
    expect(splash).toHaveStyle({
      transitionDuration: `${TRANSITION_MS}ms`,
      zIndex: ZIndex.changelogOverlay + 1,
    });
    expect(screen.getByTestId('arc-spinner')).toBeInTheDocument();
    expect(screen.getByTestId('startup-splash-status')).toHaveTextContent('Loading…');
  });

  it('is accessibility-silent when another bootstrap stage owns the announcement', () => {
    render(<StartupSplash />);

    expect(screen.queryByRole('status')).not.toBeInTheDocument();
    expect(screen.queryByTestId('startup-splash-status')).not.toBeInTheDocument();
    expect(screen.getByTestId('startup-splash')).toHaveAttribute('aria-hidden', 'true');
  });

  it('completes only for its own opacity transition', () => {
    const onExitComplete = vi.fn();
    render(<StartupSplash phase="revealing" onExitComplete={onExitComplete} />);

    const splash = screen.getByTestId('startup-splash');
    fireEvent.transitionEnd(screen.getByTestId('arc-spinner'), { propertyName: 'opacity' });
    fireEvent.transitionEnd(splash, { propertyName: 'transform' });
    expect(onExitComplete).not.toHaveBeenCalled();

    fireEvent.transitionEnd(splash, { propertyName: 'opacity' });
    expect(onExitComplete).toHaveBeenCalledTimes(1);
  });
});
