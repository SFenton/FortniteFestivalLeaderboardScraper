import { describe, it, expect, vi } from 'vitest';
import { render, fireEvent } from '@testing-library/react';
import React, { createRef } from 'react';
import SearchBar, { type SearchBarRef } from '../../components/common/SearchBar';

describe('SearchBar', () => {
  it('renders with icon and input', () => {
    const onChange = vi.fn();
    const { container } = render(
      React.createElement(SearchBar, { value: '', onChange, placeholder: 'Search...' }),
    );
    expect(container.querySelector('svg')).not.toBeNull();
    expect(container.querySelector('input')).not.toBeNull();
    expect(container.querySelector('input')?.getAttribute('placeholder')).toBe('Search...');
  });

  it('hides icon when hideIcon is true', () => {
    const onChange = vi.fn();
    const { container } = render(
      React.createElement(SearchBar, { value: '', onChange, hideIcon: true }),
    );
    expect(container.querySelector('svg')).toBeNull();
  });

  it('calls onChange when input value changes', () => {
    const onChange = vi.fn();
    const { container } = render(
      React.createElement(SearchBar, { value: '', onChange }),
    );
    const input = container.querySelector('input')!;
    fireEvent.change(input, { target: { value: 'test' } });
    expect(onChange).toHaveBeenCalledWith('test');
  });

  it('focuses input when wrapper is clicked', () => {
    const onChange = vi.fn();
    const { container } = render(
      React.createElement(SearchBar, { value: '', onChange }),
    );
    const wrapper = container.firstElementChild!;
    const input = container.querySelector('input')!;
    const focusSpy = vi.spyOn(input, 'focus');
    fireEvent.click(wrapper);
    expect(focusSpy).toHaveBeenCalled();
  });

  it('forwards onKeyDown to input', () => {
    const onKeyDown = vi.fn();
    const onChange = vi.fn();
    const { container } = render(
      React.createElement(SearchBar, { value: '', onChange, onKeyDown }),
    );
    const input = container.querySelector('input')!;
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(onKeyDown).toHaveBeenCalled();
  });

  it('forwards onFocus to input', () => {
    const onFocus = vi.fn();
    const onChange = vi.fn();
    const { container } = render(
      React.createElement(SearchBar, { value: '', onChange, onFocus }),
    );
    const input = container.querySelector('input')!;
    fireEvent.focus(input);
    expect(onFocus).toHaveBeenCalled();
  });

  it('applies custom className to wrapper', () => {
    const onChange = vi.fn();
    const { container } = render(
      React.createElement(SearchBar, { value: '', onChange, className: 'custom-wrap' }),
    );
    expect(container.firstElementChild?.classList.contains('custom-wrap')).toBe(true);
  });

  it('applies custom inputClassName to input', () => {
    const onChange = vi.fn();
    const { container } = render(
      React.createElement(SearchBar, { value: '', onChange, inputClassName: 'custom-input' }),
    );
    expect(container.querySelector('input')?.classList.contains('custom-input')).toBe(true);
  });

  it('supports enterKeyHint attribute', () => {
    const onChange = vi.fn();
    const { container } = render(
      React.createElement(SearchBar, { value: '', onChange, enterKeyHint: 'done' }),
    );
    expect(container.querySelector('input')?.getAttribute('enterkeyhint')).toBe('done');
  });

  it('exposes focus/blur via ref', () => {
    const onChange = vi.fn();
    const ref = createRef<SearchBarRef>();
    render(React.createElement(SearchBar, { ref, value: '', onChange }));
    expect(() => ref.current?.focus()).not.toThrow();
    expect(() => ref.current?.blur()).not.toThrow();
  });

  it('applies style to wrapper', () => {
    const onChange = vi.fn();
    const { container } = render(
      React.createElement(SearchBar, { value: '', onChange, style: { opacity: 0.5 } }),
    );
    expect(container.firstElementChild?.getAttribute('style')).toContain('opacity');
  });

  it('renders controlled value', () => {
    const onChange = vi.fn();
    const { container } = render(
      React.createElement(SearchBar, { value: 'hello', onChange }),
    );
    expect((container.querySelector('input') as HTMLInputElement).value).toBe('hello');
  });
});
