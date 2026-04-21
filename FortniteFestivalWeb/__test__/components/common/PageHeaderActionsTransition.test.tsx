import { act, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { FAST_FADE_MS, TRANSITION_MS } from '@festival/theme';
import PageHeaderActionsTransition from '../../../src/components/common/PageHeaderActionsTransition';
import { SettingsProvider, useSettings } from '../../../src/contexts/SettingsContext';

interface HarnessProps {
  visible: boolean;
  pageKey?: string;
}

let latestSettings: ReturnType<typeof useSettings> | null = null;

function Harness({ visible, pageKey = 'page-header-actions' }: HarnessProps) {
  latestSettings = useSettings();
  return (
    <PageHeaderActionsTransition key={pageKey} visible={visible} testId="page-header-actions-transition">
      <button type="button">Quick Links</button>
    </PageHeaderActionsTransition>
  );
}

describe('PageHeaderActionsTransition', () => {
  let scrollWidthDescriptor: PropertyDescriptor | undefined;

  beforeEach(() => {
    vi.useFakeTimers();
    latestSettings = null;
    scrollWidthDescriptor = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'scrollWidth');
    Object.defineProperty(HTMLElement.prototype, 'scrollWidth', {
      configurable: true,
      get() {
        return 120;
      },
    });
  });

  afterEach(() => {
    vi.useRealTimers();
    if (scrollWidthDescriptor) {
      Object.defineProperty(HTMLElement.prototype, 'scrollWidth', scrollWidthDescriptor);
    } else {
      delete (HTMLElement.prototype as Partial<HTMLElement>).scrollWidth;
    }
  });

  it('keeps the actions mounted during fade/collapse before unmounting on exit', () => {
    const { rerender } = render(
      <SettingsProvider>
        <Harness visible />
      </SettingsProvider>,
    );

    expect(screen.getByRole('button', { name: 'Quick Links' })).toBeDefined();

    act(() => {
      latestSettings?.updateSettings({ showButtonsInHeaderMobile: false });
    });

    rerender(
      <SettingsProvider>
        <Harness visible={false} />
      </SettingsProvider>,
    );

    expect(screen.queryByRole('button', { name: 'Quick Links' })).toBeNull();
    expect(screen.getByTestId('page-header-actions-transition')).toBeDefined();

    act(() => {
      vi.advanceTimersByTime(FAST_FADE_MS + TRANSITION_MS - 1);
    });

    expect(screen.getByTestId('page-header-actions-transition')).toBeDefined();

    act(() => {
      vi.advanceTimersByTime(1);
    });

    expect(screen.queryByTestId('page-header-actions-transition')).toBeNull();
  });

  it('expands width before revealing the actions on enter', () => {
    const { rerender } = render(
      <SettingsProvider>
        <Harness visible={false} />
      </SettingsProvider>,
    );

    expect(screen.queryByTestId('page-header-actions-transition')).toBeNull();

    act(() => {
      latestSettings?.updateSettings({ showButtonsInHeaderMobile: false });
      latestSettings?.updateSettings({ showButtonsInHeaderMobile: true });
    });

    rerender(
      <SettingsProvider>
        <Harness visible />
      </SettingsProvider>,
    );

    const wrapper = screen.getByTestId('page-header-actions-transition');
    expect(wrapper.style.maxWidth).toBe('0px');
    expect(screen.queryByRole('button', { name: 'Quick Links' })).toBeNull();

    act(() => {
      vi.advanceTimersByTime(0);
    });

    expect(wrapper.style.maxWidth).toBe('120px');
    expect(screen.queryByRole('button', { name: 'Quick Links' })).toBeNull();

    act(() => {
      vi.advanceTimersByTime(TRANSITION_MS);
    });

    expect(screen.getByRole('button', { name: 'Quick Links' })).toBeDefined();
  });

  it('does not replay the enter animation on a later page mount without another setting flip', () => {
    const { rerender } = render(
      <SettingsProvider>
        <Harness visible={false} pageKey="statistics" />
      </SettingsProvider>,
    );

    act(() => {
      latestSettings?.updateSettings({ showButtonsInHeaderMobile: false });
      latestSettings?.updateSettings({ showButtonsInHeaderMobile: true });
    });

    rerender(
      <SettingsProvider>
        <Harness visible pageKey="statistics" />
      </SettingsProvider>,
    );

    act(() => {
      vi.advanceTimersByTime(TRANSITION_MS + FAST_FADE_MS + 100);
    });

    rerender(
      <SettingsProvider>
        <Harness visible pageKey="statistics-remount" />
      </SettingsProvider>,
    );

    const wrapper = screen.getByTestId('page-header-actions-transition');
    expect(screen.getByRole('button', { name: 'Quick Links' })).toBeDefined();
    expect(wrapper.style.maxWidth).toBe('120px');
  });
});