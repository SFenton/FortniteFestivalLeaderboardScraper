import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { Colors, Layout } from '@festival/theme';
import { SelectProfilePill } from '../../../src/components/player/SelectProfilePill';

describe('SelectProfilePill', () => {
  it('renders mobile/PWA as the dark opaque labeled pill', () => {
    const onClick = vi.fn();

    render(<SelectProfilePill visible isMobile onClick={onClick} />);

    const button = screen.getByTestId('select-profile-pill');
    expect(button).toHaveAccessibleName('Select Player Profile');
    expect(button).toHaveTextContent('Select Player Profile');
    expect(button.style.backgroundColor).toBe('rgba(18, 24, 38, 0.96)');
    expect(button).toHaveStyle({
      minWidth: `${Layout.pillButtonHeight}px`,
      height: `${Layout.pillButtonHeight}px`,
      borderRadius: '999px',
      padding: '0px 12px 0px 10px',
    });
    expect(button.style.width).toBe('');

    fireEvent.click(button);

    expect(onClick).toHaveBeenCalledTimes(1);
  });

  it('keeps desktop as the purple full ActionPill', () => {
    render(<SelectProfilePill visible onClick={vi.fn()} />);

    const button = screen.getByRole('button', { name: 'Select Player Profile' });
    expect(screen.queryByTestId('select-profile-pill')).toBeNull();
    expect(button).toHaveTextContent('Select Player Profile');
    expect(button).toHaveStyle({
      backgroundColor: Colors.accentPurple,
      height: `${Layout.pillButtonHeight}px`,
      borderRadius: '999px',
    });
  });
});
