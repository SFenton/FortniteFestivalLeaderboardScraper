import { beforeAll, describe, expect, it } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router-dom';
import type { ServerInstrumentKey } from '@festival/core/api';
import InvalidScoreIcon from '../../../../src/pages/songs/components/InvalidScoreIcon';
import { loadConfirmAlert } from '../../../../src/components/lazy/secondaryControls';

beforeAll(async () => {
  await loadConfirmAlert();
});

function LocationProbe() {
  return <output data-testid="location">{useLocation().pathname}</output>;
}

function renderIcon(
  invalidInstruments: Map<ServerInstrumentKey, 'fallback' | 'no-fallback' | 'over-threshold'>,
  instrumentFilter?: ServerInstrumentKey | null,
) {
  return render(
    <MemoryRouter initialEntries={['/songs']}>
      <InvalidScoreIcon
        songTitle="Test Song"
        invalidInstruments={invalidInstruments}
        instrumentFilter={instrumentFilter}
      />
      <LocationProbe />
    </MemoryRouter>,
  );
}

describe('InvalidScoreIcon', () => {
  it('explains an over-threshold score and navigates to Settings', async () => {
    renderIcon(new Map([['Solo_Drums', 'over-threshold']]), 'Solo_Drums');
    const trigger = screen.getByRole('button', { name: /invalid score/i });

    fireEvent.click(trigger);

    const dialog = await screen.findByRole('alertdialog');
    expect(dialog).toHaveTextContent('Test Song');
    expect(dialog).toHaveTextContent('Drums');
    expect(dialog).toHaveTextContent(/CHOpt maximum/i);
    fireEvent.click(screen.getByRole('button', { name: 'Settings' }));
    expect(screen.getByTestId('location')).toHaveTextContent('/settings');
  });

  it('describes fallback and missing fallback states from the chip view', async () => {
    renderIcon(new Map([
      ['Solo_Guitar', 'fallback'],
      ['Solo_Bass', 'no-fallback'],
      ['Solo_Vocals', 'over-threshold'],
    ]));
    const trigger = screen.getByRole('button', { name: /invalid score/i });

    trigger.focus();
    fireEvent.keyDown(trigger, { key: 'Enter' });

    const dialog = await screen.findByRole('alertdialog');
    expect(dialog).toHaveTextContent('Lead');
    expect(dialog).toHaveTextContent('Bass');
    expect(dialog).toHaveTextContent('Vocals');
    expect(dialog).toHaveTextContent(/next valid score/i);
    expect(dialog).toHaveTextContent(/no valid other score/i);
    expect(dialog).toHaveTextContent(/filter/i);
  });

  it('opens from Space and limits copy to the selected instrument', async () => {
    renderIcon(new Map([
      ['Solo_Guitar', 'fallback'],
      ['Solo_Bass', 'no-fallback'],
    ]), 'Solo_Bass');
    const trigger = screen.getByRole('button', { name: /invalid score/i });

    fireEvent.keyDown(trigger, { key: ' ' });

    const dialog = await screen.findByRole('alertdialog');
    expect(dialog).toHaveTextContent('Bass');
    expect(dialog).not.toHaveTextContent('Lead');
    expect(screen.getByRole('button', { name: 'OK' })).toBeInTheDocument();
  });
});
