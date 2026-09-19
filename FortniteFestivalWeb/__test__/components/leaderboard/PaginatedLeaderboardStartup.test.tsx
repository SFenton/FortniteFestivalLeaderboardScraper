import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { PaginatedLeaderboard } from '../../../src/components/leaderboard/PaginatedLeaderboard';
import { ScrollContainerProvider } from '../../../src/contexts/ScrollContainerContext';
import { StartupEntranceProvider } from '../../../src/contexts/StartupEntranceContext';

function renderLeaderboard(complete: boolean) {
  return (
    <StartupEntranceProvider complete={complete}>
      <MemoryRouter>
        <ScrollContainerProvider>
          <PaginatedLeaderboard
            entries={[]}
            page={1}
            totalPages={1}
            onGoToPage={() => {}}
            entryKey={(entry: { id: string }) => entry.id}
            isPlayerEntry={() => false}
            renderRow={() => null}
            entryLinkTo={() => '/'}
            hasPlayerFooter
            renderPlayerFooter={props => <div {...props}>Tracked player</div>}
            loading={false}
            cached
            isMobile={false}
            hasFab={false}
          />
        </ScrollContainerProvider>
      </MemoryRouter>
    </StartupEntranceProvider>
  );
}

describe('PaginatedLeaderboard startup entrance', () => {
  it('keeps its direct body portal unmounted until startup completes', () => {
    const { rerender } = render(renderLeaderboard(false));

    expect(screen.queryByText('Tracked player')).not.toBeInTheDocument();

    rerender(renderLeaderboard(true));

    expect(screen.getByText('Tracked player')).toBeInTheDocument();
  });
});
