import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, useNavigate } from 'react-router-dom';
import type { ComponentProps } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import ShellScrollRestoration from '../../../src/components/shell/ShellScrollRestoration';

const scrollElement = document.createElement('div');
const scrollTo = vi.fn();
scrollElement.scrollTo = scrollTo;
const scrollContainerRef = { current: scrollElement };

vi.mock('../../../src/contexts/ScrollContainerContext', () => ({
  useScrollContainer: () => scrollContainerRef,
}));

function Harness({
  loadSuggestionsPage,
}: {
  loadSuggestionsPage: ComponentProps<typeof ShellScrollRestoration>['loadSuggestionsPage'];
}) {
  const navigate = useNavigate();
  return (
    <>
      <button type="button" onClick={() => navigate('/settings')}>Settings</button>
      <ShellScrollRestoration
        layoutKey="standard"
        loadSuggestionsPage={loadSuggestionsPage}
      />
    </>
  );
}

describe('ShellScrollRestoration', () => {
  beforeEach(() => {
    scrollTo.mockClear();
    scrollElement.scrollTop = 0;
  });

  it('scrolls ordinary routes to the top', async () => {
    const loadSuggestionsPage = vi.fn();
    render(
      <MemoryRouter initialEntries={['/settings']}>
        <Harness loadSuggestionsPage={loadSuggestionsPage} />
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(scrollTo).toHaveBeenCalledWith(0, 0);
    });
    expect(loadSuggestionsPage).not.toHaveBeenCalled();
  });

  it('starts and stops lazy Suggestions restoration across navigation', async () => {
    const cleanup = vi.fn();
    const beginSuggestionsScrollRestoration = vi.fn(() => cleanup);
    const loadSuggestionsPage = vi.fn().mockResolvedValue({
      beginSuggestionsScrollRestoration,
    });
    render(
      <MemoryRouter initialEntries={['/suggestions']}>
        <Harness loadSuggestionsPage={loadSuggestionsPage} />
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(beginSuggestionsScrollRestoration).toHaveBeenCalledWith(
        scrollElement,
      );
    });
    fireEvent.click(screen.getByRole('button', { name: 'Settings' }));

    await waitFor(() => {
      expect(cleanup).toHaveBeenCalled();
      expect(scrollTo).toHaveBeenCalledWith(0, 0);
    });
  });
});
