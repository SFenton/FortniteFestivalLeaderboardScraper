import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import CatalogUpdateBanner from '../../../src/components/page/CatalogUpdateBanner';
import { TestProviders } from '../../helpers/TestProviders';

function renderBanner(count: number) {
  return render(
    <TestProviders>
      <CatalogUpdateBanner count={count} />
    </TestProviders>,
  );
}

describe('CatalogUpdateBanner', () => {
  it('explains that detected catalog changes remain publication-bound', () => {
    renderBanner(3);

    expect(screen.getByText('3 catalog updates detected')).toBeTruthy();
    expect(screen.getByText(
      /waiting for a leaderboard update to publish/i,
    )).toBeTruthy();
  });

  it('uses the singular count label', () => {
    renderBanner(1);

    expect(screen.getByText('1 catalog update detected')).toBeTruthy();
  });
});
