import { act, render, screen } from '@testing-library/react';
import { beforeEach, afterEach, describe, expect, it, vi } from 'vitest';
import { FAST_FADE_MS, TRANSITION_MS } from '@festival/theme';
import PageHeaderTransition from '../../../src/components/common/PageHeaderTransition';
import { SettingsProvider, useSettings } from '../../../src/contexts/SettingsContext';

interface HarnessProps {
  visible: boolean;
  pageKey?: string;
}

let latestSettings: ReturnType<typeof useSettings> | null = null;

function Harness({ visible, pageKey = 'page-header' }: HarnessProps) {
  latestSettings = useSettings();
  return (
    <PageHeaderTransition key={pageKey} visible={visible} testId="page-header-transition">
      <button type="button">Quick Links</button>
    </PageHeaderTransition>
  );
}

describe('PageHeaderTransition', () => {
  let scrollHeightDescriptor: PropertyDescriptor | undefined;

  beforeEach(() => {
    vi.useFakeTimers();
    latestSettings = null;
    scrollHeightDescriptor = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'scrollHeight');
    Object.defineProperty(HTMLElement.prototype, 'scrollHeight', {
      configurable: true,
      get() {
        return 80;
      },
    });
  });

  afterEach(() => {
    vi.useRealTimers();
    if (scrollHeightDescriptor) {
      Object.defineProperty(HTMLElement.prototype, 'scrollHeight', scrollHeightDescriptor);
    } else {
      delete (HTMLElement.prototype as Partial<HTMLElement>).scrollHeight;
    }
  });

  it('keeps the header mounted during fade/collapse before unmounting on exit', () => {
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
    expect(screen.getByTestId('page-header-transition')).toBeDefined();

    act(() => {
      vi.advanceTimersByTime(FAST_FADE_MS + TRANSITION_MS - 1);
    });

    expect(screen.getByTestId('page-header-transition')).toBeDefined();

    act(() => {
      vi.advanceTimersByTime(1);
    });

    expect(screen.queryByTestId('page-header-transition')).toBeNull();
  });

  it('expands the area before revealing the header content on enter', () => {
    const { rerender } = render(
      <SettingsProvider>
        <Harness visible={false} />
      </SettingsProvider>,
    );

    expect(screen.queryByTestId('page-header-transition')).toBeNull();

    act(() => {
      latestSettings?.updateSettings({ showButtonsInHeaderMobile: false });
      latestSettings?.updateSettings({ showButtonsInHeaderMobile: true });
    });

    rerender(
      <SettingsProvider>
        <Harness visible />
      </SettingsProvider>,
    );

    const wrapper = screen.getByTestId('page-header-transition');
    expect(wrapper.style.maxHeight).toBe('0px');
    expect(screen.queryByRole('button', { name: 'Quick Links' })).toBeNull();

    act(() => {
      vi.advanceTimersByTime(0);
    });

    expect(wrapper.style.maxHeight).toBe('80px');
    expect(screen.queryByRole('button', { name: 'Quick Links' })).toBeNull();

    act(() => {
      vi.advanceTimersByTime(TRANSITION_MS);
    });

    expect(screen.getByRole('button', { name: 'Quick Links' })).toBeDefined();
  });

  it('does not replay the enter animation on a later page mount without another setting flip', () => {
    const { rerender } = render(
      <SettingsProvider>
        <Harness visible={false} pageKey="settings" />
      </SettingsProvider>,
    );

    act(() => {
      latestSettings?.updateSettings({ showButtonsInHeaderMobile: false });
      latestSettings?.updateSettings({ showButtonsInHeaderMobile: true });
    });

    rerender(
      <SettingsProvider>
        <Harness visible pageKey="settings" />
      </SettingsProvider>,
    );

    act(() => {
      vi.advanceTimersByTime(TRANSITION_MS + 500);
    });

    rerender(
      <SettingsProvider>
        <Harness visible pageKey="songs" />
      </SettingsProvider>,
    );

    const wrapper = screen.getByTestId('page-header-transition');
    expect(screen.getByRole('button', { name: 'Quick Links' })).toBeDefined();
    expect(wrapper.style.maxHeight).not.toBe('0px');
  });
});