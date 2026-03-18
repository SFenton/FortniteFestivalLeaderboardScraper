import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';

vi.mock('../../hooks/data/useVersions', () => ({
  APP_VERSION: '1.2.3',
}));
vi.mock('../../changelog', () => ({
  changelog: [
    { sections: [{ title: 'v1.2.3 — March 2025', items: ['New feature A', 'Bug fix B'] }] },
    { sections: [{ title: 'v1.1.0 — February 2025', items: ['Initial release'] }] },
  ],
}));
vi.mock('../../hooks/ui/useScrollMask', () => ({
  useScrollMask: () => vi.fn(),
}));

beforeEach(() => {
  vi.spyOn(window, 'requestAnimationFrame').mockImplementation((cb) => { cb(0); return 0; });
  vi.spyOn(window, 'cancelAnimationFrame').mockImplementation(() => {});
});

import ChangelogModal from '../../components/modals/ChangelogModal';

describe('ChangelogModal', () => {
  it('renders changelog entries', () => {
    render(<ChangelogModal onDismiss={vi.fn()} />);
    expect(screen.getByText(/v1\.2\.3/)).toBeTruthy();
    expect(screen.getByText('New feature A')).toBeTruthy();
    expect(screen.getByText('Bug fix B')).toBeTruthy();
  });

  it('calls onDismiss when overlay is clicked', () => {
    const onDismiss = vi.fn();
    const { container } = render(<ChangelogModal onDismiss={onDismiss} />);
    const overlay = container.firstElementChild!;
    fireEvent.click(overlay);
    expect(onDismiss).toHaveBeenCalled();
  });

  it('calls onDismiss when Escape is pressed', () => {
    const onDismiss = vi.fn();
    render(<ChangelogModal onDismiss={onDismiss} />);
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(onDismiss).toHaveBeenCalled();
  });

  it('calls onDismiss when close button is clicked', () => {
    const onDismiss = vi.fn();
    render(<ChangelogModal onDismiss={onDismiss} />);
    const closeBtn = screen.getByLabelText('Close');
    fireEvent.click(closeBtn);
    expect(onDismiss).toHaveBeenCalled();
  });

  it('renders second changelog entry', () => {
    render(<ChangelogModal onDismiss={vi.fn()} />);
    expect(screen.getByText(/v1\.1\.0/)).toBeTruthy();
    expect(screen.getByText('Initial release')).toBeTruthy();
  });
});
