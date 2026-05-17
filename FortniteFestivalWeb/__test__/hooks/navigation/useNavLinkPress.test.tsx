import { fireEvent, render, screen } from '@testing-library/react';
import type { ComponentProps } from 'react';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import { useNavLinkPress } from '../../../src/hooks/navigation/useNavLinkPress';

function LocationProbe() {
  const location = useLocation();
  return <div data-testid="location">{location.pathname}</div>;
}

function TestLink({
  disabled,
  download,
  onNavigate = vi.fn(),
  target,
  to = '/target',
}: {
  disabled?: boolean;
  download?: boolean | string;
  onNavigate?: () => void;
  target?: string;
  to?: string;
}) {
  const linkPress = useNavLinkPress<HTMLAnchorElement>({
    to,
    disabled,
    download,
    onNavigate,
    target,
  });

  return (
    <a
      href="#"
      target={target}
      download={download}
      data-testid="link"
      data-pressed={linkPress.isPressed ? 'true' : undefined}
      {...linkPress.linkPressHandlers}
    >
      Go
    </a>
  );
}

function renderLink(props: ComponentProps<typeof TestLink> = {}) {
  return render(
    <MemoryRouter initialEntries={['/start']}>
      <TestLink {...props} />
      <LocationProbe />
    </MemoryRouter>,
  );
}

describe('useNavLinkPress', () => {
  it('navigates internal touch links on pointerup and suppresses the synthetic click', () => {
    const onNavigate = vi.fn();
    renderLink({ onNavigate });
    const link = screen.getByTestId('link');

    fireEvent.pointerDown(link, { button: 0, isPrimary: true, pointerId: 1, pointerType: 'touch', clientX: 10, clientY: 10 });
    expect(link).toHaveAttribute('data-pressed', 'true');

    fireEvent.pointerUp(link, { button: 0, isPrimary: true, pointerId: 1, pointerType: 'touch', clientX: 10, clientY: 10 });
    expect(screen.getByTestId('location')).toHaveTextContent('/target');
    expect(onNavigate).toHaveBeenCalledTimes(1);
    expect(link).not.toHaveAttribute('data-pressed');

    fireEvent.click(link);
    expect(onNavigate).toHaveBeenCalledTimes(1);
  });

  it('cancels pointer navigation when movement exceeds the threshold', () => {
    const onNavigate = vi.fn();
    renderLink({ onNavigate });
    const link = screen.getByTestId('link');

    fireEvent.pointerDown(link, { button: 0, isPrimary: true, pointerId: 1, pointerType: 'touch', clientX: 10, clientY: 10 });
    fireEvent.pointerMove(link, { button: 0, isPrimary: true, pointerId: 1, pointerType: 'touch', clientX: 10, clientY: 40 });
    fireEvent.pointerUp(link, { button: 0, isPrimary: true, pointerId: 1, pointerType: 'touch', clientX: 10, clientY: 40 });

    expect(screen.getByTestId('location')).toHaveTextContent('/start');
    expect(onNavigate).not.toHaveBeenCalled();
    expect(link).not.toHaveAttribute('data-pressed');
  });

  it('leaves new-tab and download links to native browser behavior', () => {
    const newTabNavigate = vi.fn();
    renderLink({ onNavigate: newTabNavigate, target: '_blank' });
    const newTabLink = screen.getByTestId('link');

    fireEvent.pointerDown(newTabLink, { button: 0, isPrimary: true, pointerId: 1, pointerType: 'touch', clientX: 10, clientY: 10 });
    fireEvent.pointerUp(newTabLink, { button: 0, isPrimary: true, pointerId: 1, pointerType: 'touch', clientX: 10, clientY: 10 });

    expect(screen.getByTestId('location')).toHaveTextContent('/start');
    expect(newTabNavigate).not.toHaveBeenCalled();
  });

  it('still runs the fallback navigation hook for normal mouse clicks', () => {
    const onNavigate = vi.fn();
    renderLink({ onNavigate });

    fireEvent.click(screen.getByTestId('link'));

    expect(onNavigate).toHaveBeenCalledTimes(1);
    expect(screen.getByTestId('location')).toHaveTextContent('/start');
  });
});
