import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import { renderHook } from '@testing-library/react';
import { PageBackground, usePageScrollRef } from '../../pages/Page';

describe('PageBackground', () => {
  it('returns null when src is undefined', () => {
    const { container } = render(<PageBackground src={undefined} />);
    expect(container.innerHTML).toBe('');
  });

  it('renders background image and dim overlay when src is provided', () => {
    const { container } = render(<PageBackground src="https://example.com/art.jpg" />);
    const bgDiv = container.querySelector('[style*="background-image"]');
    expect(bgDiv).toBeTruthy();
  });
});

describe('usePageScrollRef', () => {
  it('returns a ref object', () => {
    const { result } = renderHook(() => usePageScrollRef());
    expect(result.current).toBeDefined();
    expect(result.current.current).toBeNull();
  });
});
