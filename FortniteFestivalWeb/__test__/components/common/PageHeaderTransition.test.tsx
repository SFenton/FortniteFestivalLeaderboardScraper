import { act, render, screen } from '@testing-library/react';
import { beforeEach, afterEach, describe, expect, it, vi } from 'vitest';
import { FAST_FADE_MS, TRANSITION_MS } from '@festival/theme';
import PageHeaderTransition from '../../../src/components/common/PageHeaderTransition';

describe('PageHeaderTransition', () => {
  let scrollHeightDescriptor: PropertyDescriptor | undefined;

  beforeEach(() => {
    vi.useFakeTimers();
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
      <PageHeaderTransition visible testId="page-header-transition">
        <button type="button">Quick Links</button>
      </PageHeaderTransition>,
    );

    expect(screen.getByRole('button', { name: 'Quick Links' })).toBeDefined();

    rerender(
      <PageHeaderTransition visible={false} testId="page-header-transition">
        <button type="button">Quick Links</button>
      </PageHeaderTransition>,
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
      <PageHeaderTransition visible={false} testId="page-header-transition">
        <button type="button">Quick Links</button>
      </PageHeaderTransition>,
    );

    expect(screen.queryByTestId('page-header-transition')).toBeNull();

    rerender(
      <PageHeaderTransition visible testId="page-header-transition">
        <button type="button">Quick Links</button>
      </PageHeaderTransition>,
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
});