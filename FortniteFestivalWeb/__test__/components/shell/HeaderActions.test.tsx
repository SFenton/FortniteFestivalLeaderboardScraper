import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import HeaderActions from '../../../src/components/shell/HeaderActions';
import { GeneralSize } from '@festival/theme';

describe('HeaderActions', () => {
  it('uses a native disabled notification button while notifications are loading', () => {
    const onOpenNotifications = vi.fn();

    render(
      <HeaderActions
        testIdPrefix="mobile"
        onOpenSearch={vi.fn()}
        onOpenNotifications={onOpenNotifications}
        notificationVisualState="spinner"
      />,
    );

    const button = screen.getByTestId('mobile-notifications') as HTMLButtonElement;
    expect(button).toBeDisabled();
    expect(button.tabIndex).toBe(-1);

    fireEvent.click(button);
    expect(onOpenNotifications).not.toHaveBeenCalled();
  });

  it('keeps the notification button enabled once the icon state is ready', () => {
    const onOpenNotifications = vi.fn();

    render(
      <HeaderActions
        testIdPrefix="mobile"
        onOpenSearch={vi.fn()}
        onOpenNotifications={onOpenNotifications}
        notificationVisualState="icon"
      />,
    );

    const button = screen.getByTestId('mobile-notifications') as HTMLButtonElement;
    expect(button).not.toBeDisabled();

    fireEvent.click(button);
    expect(onOpenNotifications).toHaveBeenCalledTimes(1);
  });

  it('opens header search from touch pointerup without double firing on click', () => {
    const onOpenSearch = vi.fn();

    render(
      <HeaderActions
        testIdPrefix="mobile"
        onOpenSearch={onOpenSearch}
      />,
    );

    const button = screen.getByTestId('mobile-search') as HTMLButtonElement;
    fireEvent.pointerDown(button, { pointerId: 1, pointerType: 'touch', button: 0, clientX: 320, clientY: 100 });
    fireEvent.pointerUp(button, { pointerId: 1, pointerType: 'touch', button: 0, clientX: 320, clientY: 101 });

    expect(onOpenSearch).toHaveBeenCalledTimes(1);

    fireEvent.click(button);
    expect(onOpenSearch).toHaveBeenCalledTimes(1);
  });

  it('uses thumb-sized header action targets', () => {
    render(
      <HeaderActions
        testIdPrefix="mobile"
        onProfileAction={vi.fn()}
        onOpenSearch={vi.fn()}
        onOpenNotifications={vi.fn()}
      />,
    );

    for (const testId of ['mobile-profile', 'mobile-search', 'mobile-notifications']) {
      const button = screen.getByTestId(testId) as HTMLButtonElement;
      expect(button.style.width).toBe(`${GeneralSize.thumb}px`);
      expect(button.style.height).toBe(`${GeneralSize.thumb}px`);
    }
  });
});