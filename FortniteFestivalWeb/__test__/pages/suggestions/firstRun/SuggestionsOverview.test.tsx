import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { suggestionsOverviewSlide } from '../../../../src/pages/suggestions/components/firstRun/SuggestionsOverview';

describe('suggestionsOverviewSlide', () => {
  it('has correct id', () => {
    expect(suggestionsOverviewSlide.id).toBe('suggestions-overview');
  });

  it('has version 1', () => {
    expect(suggestionsOverviewSlide.version).toBe(1);
  });

  it('has title and description', () => {
    expect(suggestionsOverviewSlide.title).toBe('firstRun.suggestions.overview.title');
    expect(suggestionsOverviewSlide.description).toBe('firstRun.suggestions.overview.description');
  });

  it('has contentStaggerCount of 1', () => {
    expect(suggestionsOverviewSlide.contentStaggerCount).toBe(1);
  });

  it('has no gate', () => {
    expect(suggestionsOverviewSlide.gate).toBeUndefined();
  });

  it('render() returns JSX', () => {
    const el = suggestionsOverviewSlide.render();
    expect(el).toBeTruthy();
  });
});

describe('SuggestionsPreview (rendered via slide)', () => {
  it('renders category labels', () => {
    render(suggestionsOverviewSlide.render());
    expect(screen.getByText('Close to Full Combo')).toBeTruthy();
    expect(screen.getByText('Top 5% Possible')).toBeTruthy();
    expect(screen.getByText('Unplayed on Bass')).toBeTruthy();
  });

  it('renders category tags', () => {
    render(suggestionsOverviewSlide.render());
    expect(screen.getByText('FC Gap')).toBeTruthy();
    expect(screen.getByText('Climb')).toBeTruthy();
    expect(screen.getByText('New')).toBeTruthy();
  });

  it('renders 3 cards', () => {
    render(suggestionsOverviewSlide.render());
    // Each card has a label text + tag text — verify all 3 sets are present
    expect(screen.getAllByText(/FC Gap|Climb|New/).length).toBe(3);
  });
});
