import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render } from '@testing-library/react';
import MathTex from '../../../src/components/common/Math';

describe('MathTex', () => {
  beforeEach(() => {
    vi.spyOn(console, 'warn').mockImplementation(() => {});
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('renders inline math without a block wrapper', () => {
    const { container } = render(<MathTex tex="x + y" />);

    expect(container.firstElementChild?.tagName).toBe('SPAN');
    expect(container.querySelector('.katex')).toBeTruthy();
    expect(container.querySelector('.katex-display')).toBeNull();
  });

  it('contains block math within a horizontally scrollable wrapper', () => {
    const { container } = render(<MathTex tex="\\frac{\\sum_i p_i \\cdot \\text{Weight}_i}{\\sum_i \\text{Weight}_i}" block />);
    const wrapper = container.firstElementChild as HTMLElement;

    expect(wrapper.tagName).toBe('DIV');
    expect(wrapper.style.width).toBe('100%');
    expect(wrapper.style.maxWidth).toBe('100%');
    expect(wrapper.style.minWidth).toBe('0px');
    expect(wrapper.style.overflowX).toBe('auto');
    expect(wrapper.style.overflowY).toBe('visible');
    expect(wrapper.style.paddingTop).toBe('2px');
    expect(wrapper.style.paddingBottom).toBe('2px');
    expect(container.querySelector('.katex-display')).toBeTruthy();
  });

  it('keeps invalid TeX from throwing', () => {
    expect(() => render(<MathTex tex="\\notARealCommand{" block />)).not.toThrow();
  });
});