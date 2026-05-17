import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import PageHeader from '../../../src/components/common/PageHeader';
import { BandFilterActionProvider } from '../../../src/contexts/BandFilterActionContext';

describe('PageHeader band filter action', () => {
  it('renders string titles through the marquee heading wrapper', () => {
    render(
      <BandFilterActionProvider value={{ visible: false, label: 'Filter Band Type', selectedInstruments: [], onPress: vi.fn() }}>
        <PageHeader title="SFentonX + Phankie.ToT + Long Band Name" />
      </BandFilterActionProvider>,
    );

    const heading = screen.getByRole('heading', { level: 1, name: 'SFentonX + Phankie.ToT + Long Band Name' });
    expect(heading.parentElement).toHaveStyle({ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' });
  });

  it('renders subtitles through the marquee text wrapper', () => {
    render(
      <BandFilterActionProvider value={{ visible: false, label: 'Filter Band Type', selectedInstruments: [], onPress: vi.fn() }}>
        <PageHeader title="SFentonX + Phankie.ToT" subtitle="Duos with a very long appearance summary" />
      </BandFilterActionProvider>,
    );

    const subtitle = screen.getByText('Duos with a very long appearance summary');
    expect(subtitle.tagName).toBe('P');
    expect(subtitle.parentElement).toHaveStyle({ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' });
  });

  it('preserves custom title nodes', () => {
    render(
      <BandFilterActionProvider value={{ visible: false, label: 'Filter Band Type', selectedInstruments: [], onPress: vi.fn() }}>
        <PageHeader title={<h2>Custom Header</h2>} />
      </BandFilterActionProvider>,
    );

    expect(screen.getByRole('heading', { level: 2, name: 'Custom Header' })).toBeTruthy();
    expect(screen.queryByRole('heading', { level: 1, name: 'Custom Header' })).toBeNull();
  });

  it('renders the band filter pill before existing header actions', () => {
    render(
      <BandFilterActionProvider value={{ visible: true, label: 'Filter Band Type', selectedInstruments: [], onPress: vi.fn() }}>
        <PageHeader title="Songs" actions={<button type="button">Quick Links</button>} />
      </BandFilterActionProvider>,
    );

    const buttons = screen.getAllByRole('button');
    expect(buttons).toHaveLength(2);
    expect(buttons[0]?.getAttribute('aria-label')).toBe('Filter Band Type');
    expect(buttons[1]?.textContent).toBe('Quick Links');
  });

  it('does not render the band filter pill when the action is hidden', () => {
    render(
      <BandFilterActionProvider value={{ visible: false, label: 'Filter Band Type', selectedInstruments: [], onPress: vi.fn() }}>
        <PageHeader title="Songs" actions={<button type="button">Quick Links</button>} />
      </BandFilterActionProvider>,
    );

    expect(screen.queryByTestId('band-filter-pill')).toBeNull();
    expect(screen.getByRole('button', { name: 'Quick Links' })).toBeTruthy();
  });

  it('passes the applied band type into the shared band filter pill', () => {
    render(
      <BandFilterActionProvider value={{
        visible: true,
        label: 'Lead / Bass',
        selectedInstruments: ['Solo_Guitar', 'Solo_Bass'],
        appliedFilter: {
          bandId: 'band-1',
          bandType: 'Band_Duets',
          teamKey: 'acct-a:acct-b',
          comboId: 'Solo_Guitar+Solo_Bass',
          assignments: [
            { accountId: 'acct-a', instrument: 'Solo_Guitar' },
            { accountId: 'acct-b', instrument: 'Solo_Bass' },
          ],
        },
        onPress: vi.fn(),
      }}>
        <PageHeader title="Songs" />
      </BandFilterActionProvider>,
    );

    expect(screen.getByRole('button', { name: 'Lead / Bass' })).toBeTruthy();
    expect(screen.getByText('Duos')).toBeTruthy();
  });
});
